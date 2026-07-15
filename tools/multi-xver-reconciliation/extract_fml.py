"""FML (FHIR Mapping Language) extractor for the R4B->R5 pair.

Reads every *.fml StructureMap in C:\\git\\fhir-cross-version\\input\\R4BtoR5\\,
parses the group call-graph, and resolves FULL element paths by recursion from
each root group. Emits element-level correspondence records (Renamed / Mapped)
via the shared contract module. READ-ONLY: never writes to any source file.

Python 3.13 stdlib only.
"""
from __future__ import annotations

import os
import re
from collections import Counter

import contract as C

FML_DIR = os.path.join(C.XVER_INPUT, "R4BtoR5")
MAX_DEPTH = 15

# --------------------------------------------------------------------------
# String/brace-aware scanning helpers
# --------------------------------------------------------------------------
_OPEN = "([{"
_CLOSE = ")]}"


def strip_comments(text):
    """Remove // line comments and /// metadata, but NOT // inside quotes
    (URLs live inside single/double quotes). Newlines preserved."""
    out = []
    i, n = 0, len(text)
    q = None
    while i < n:
        c = text[i]
        if q:
            out.append(c)
            if c == q:
                q = None
            i += 1
            continue
        if c in "'\"":
            q = c
            out.append(c)
            i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            # skip to end of line
            while i < n and text[i] != "\n":
                i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def depth_split(s, sep):
    """Split s on sep at bracket-depth 0, outside of quotes. sep may be
    multi-char (e.g. '->')."""
    parts = []
    buf = []
    depth = 0
    q = None
    i, n = 0, len(s)
    sl = len(sep)
    while i < n:
        c = s[i]
        if q:
            buf.append(c)
            if c == q:
                q = None
            i += 1
            continue
        if c in "'\"":
            q = c
            buf.append(c)
            i += 1
            continue
        if c in _OPEN:
            depth += 1
            buf.append(c)
            i += 1
            continue
        if c in _CLOSE:
            depth -= 1
            buf.append(c)
            i += 1
            continue
        if depth == 0 and s.startswith(sep, i):
            parts.append("".join(buf))
            buf = []
            i += sl
            continue
        buf.append(c)
        i += 1
    parts.append("".join(buf))
    return parts


def find_word(s, word):
    """Return index of whole-word `word` at bracket-depth 0 outside quotes,
    else -1."""
    depth = 0
    q = None
    i, n = 0, len(s)
    w = len(word)
    while i < n:
        c = s[i]
        if q:
            if c == q:
                q = None
            i += 1
            continue
        if c in "'\"":
            q = c
            i += 1
            continue
        if c in _OPEN:
            depth += 1
            i += 1
            continue
        if c in _CLOSE:
            depth -= 1
            i += 1
            continue
        if depth == 0 and s.startswith(word, i):
            before = s[i - 1] if i > 0 else " "
            after = s[i + w] if i + w < n else " "
            ok_b = not (before.isalnum() or before == "_")
            ok_a = not (after.isalnum() or after == "_")
            if ok_b and ok_a:
                return i
        i += 1
    return -1


def extract_block(s, start):
    """s[start] == '{'; return (inner_content, index_after_matching_close)."""
    depth = 0
    q = None
    i, n = start, len(s)
    while i < n:
        c = s[i]
        if q:
            if c == q:
                q = None
            i += 1
            continue
        if c in "'\"":
            q = c
            i += 1
            continue
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return s[start + 1:i], i + 1
        i += 1
    return s[start + 1:], n


# --------------------------------------------------------------------------
# Group + statement parsing
# --------------------------------------------------------------------------
_HEADER = re.compile(r"group\s+(\w+)\s*\(([^)]*)\)[^{]*\{", re.S)
_LEAD = re.compile(r"\s*(\w+)((?:\.\w+)*)")
_ASNAME = re.compile(r"\s*as\s+(\w+)")
_TRANSLATE = re.compile(r"translate\s*\([^,]*,\s*'([^']+)'")


class Group:
    __slots__ = ("name", "params", "stmts", "file", "typed")

    def __init__(self, name, params, stmts, file, typed):
        self.name = name          # group name
        self.params = params      # [(role, varname), ...]
        self.stmts = stmts        # [Stmt, ...]
        self.file = file          # basename of source .fml
        # typed == a datatype/resource group ("group X(source src : XR4B, ...)")
        # untyped == a backbone group ("group XChild(source src, target tgt)").
        # Typed groups are their own roots (resolved once); untyped backbone
        # groups are expanded inline (concrete paths) via `then`.
        self.typed = typed


class Stmt:
    __slots__ = (
        "src_var", "src_fields", "src_alias",
        "tgt_segs",             # [(var, fields, alias), ...] sequential
        "tgt_primary",          # (var, fields) or None -> emit target leaf
        "translate_url",
        "then",                 # None | ("named", name, [args]) | ("inline", [Stmt])
    )


def iter_group_headers(text):
    """Yield (name, params_str, body, ) for each top-level group."""
    i, n = 0, len(text)
    q = None
    depth = 0
    while i < n:
        c = text[i]
        if q:
            if c == q:
                q = None
            i += 1
            continue
        if c in "'\"":
            q = c
            i += 1
            continue
        if c in "{":
            depth += 1
            i += 1
            continue
        if c in "}":
            depth -= 1
            i += 1
            continue
        if depth == 0 and text.startswith("group", i):
            before = text[i - 1] if i > 0 else " "
            if not (before.isalnum() or before == "_"):
                m = _HEADER.match(text, i)
                if m:
                    brace_idx = m.end() - 1
                    body, after = extract_block(text, brace_idx)
                    yield m.group(1), m.group(2), body
                    i = after
                    continue
        i += 1


def parse_params(params_str):
    params = []
    typed = False
    for seg in params_str.split(","):
        seg = seg.strip()
        if not seg:
            continue
        if ":" in seg:
            typed = True
        toks = seg.replace(":", " ").split()
        if len(toks) >= 2 and toks[0] in ("source", "target"):
            params.append((toks[0], toks[1]))
    return params, typed


def parse_side(expr):
    """From a source/target sub-expression, return (var, fields, alias).
    fields includes leading dots (e.g. '.coding') or ''. alias may be None."""
    e = expr
    widx = find_word(e, "where")
    if widx >= 0:
        e = e[:widx]
    alias = None
    aidx = find_word(e, "as")
    if aidx >= 0:
        am = _ASNAME.match(e[aidx:])
        if am:
            alias = am.group(1)
        e = e[:aidx]
    m = _LEAD.match(e)
    if not m:
        return None, "", alias
    return m.group(1), m.group(2), alias


def parse_then(thenpart):
    """thenpart is text right after top-level `then`. Return
    ('inline', body) or ('named', name, args) or None."""
    t = thenpart.lstrip()
    if not t:
        return None
    if t[0] == "{":
        body, _ = extract_block(t, 0)
        return ("inline", body)
    m = re.match(r"(\w+)\s*\(([^)]*)\)", t)
    if m:
        args = [a.strip() for a in m.group(2).split(",") if a.strip()]
        return ("named", m.group(1), args)
    return None


def parse_statements(body):
    """Parse a group/inline body into a list of Stmt."""
    stmts = []
    for raw in depth_split(body, ";"):
        s = raw.strip()
        if not s:
            continue
        st = _parse_statement(s)
        if st is not None:
            stmts.append(st)
    return stmts


def _parse_statement(s):
    st = Stmt()
    st.src_var = None
    st.src_fields = ""
    st.src_alias = None
    st.tgt_segs = []
    st.tgt_primary = None
    st.translate_url = None
    st.then = None

    # split off the then-clause (first top-level `then`)
    tidx = find_word(s, "then")
    if tidx >= 0:
        head = s[:tidx]
        thenpart = s[tidx + 4:]
    else:
        head = s
        thenpart = None

    # split head at top-level '->'
    arrow = depth_split(head, "->")
    if len(arrow) >= 2:
        src_expr = arrow[0]
        tgt_expr = "->".join(arrow[1:])
    else:
        src_expr = arrow[0]
        tgt_expr = None

    st.src_var, st.src_fields, st.src_alias = parse_side(src_expr)

    if tgt_expr is not None:
        for seg in depth_split(tgt_expr, ","):
            if not seg.strip():
                continue
            var, fields, alias = parse_side(seg)
            if var is None:
                continue
            st.tgt_segs.append((var, fields, alias))
            if st.tgt_primary is None and fields:
                st.tgt_primary = (var, fields)

    tm = _TRANSLATE.search(s)
    if tm:
        st.translate_url = tm.group(1)

    if thenpart is not None:
        parsed = parse_then(thenpart)
        if parsed and parsed[0] == "inline":
            st.then = ("inline", parse_statements(parsed[1]))
        elif parsed and parsed[0] == "named":
            st.then = ("named", parsed[1], parsed[2])

    return st


# --------------------------------------------------------------------------
# Load all groups
# --------------------------------------------------------------------------
def load_groups():
    groups = {}
    invoked = set()
    files = sorted(
        f for f in os.listdir(FML_DIR) if f.lower().endswith(".fml")
    )
    for fname in files:
        path = os.path.join(FML_DIR, fname)
        with open(path, "r", encoding="utf-8") as fh:
            text = fh.read()
        text = strip_comments(text)
        for name, params_str, body in iter_group_headers(text):
            stmts = parse_statements(body)
            params, typed = parse_params(params_str)
            groups[name] = Group(name, params, stmts, fname, typed)
            _collect_invoked(stmts, invoked)
    return groups, invoked, files


def _collect_invoked(stmts, invoked):
    for st in stmts:
        if st.then and st.then[0] == "named":
            invoked.add(st.then[1])
        elif st.then and st.then[0] == "inline":
            _collect_invoked(st.then[1], invoked)


# --------------------------------------------------------------------------
# Resolution (recursive full-path expansion)
# --------------------------------------------------------------------------
class Resolver:
    def __init__(self, groups):
        self.groups = groups
        self.records = {}          # key -> record dict
        self.fallback_keys = set()  # keys emitted only under fallback
        self.covered = set()        # group names entered under a root
        self.undefined = set()      # named then targets not defined

    def emit(self, src_path, tgt_path, gdef, gname, root, url, fallback):
        leaf_s = src_path.rsplit(".", 1)[-1]
        leaf_t = tgt_path.rsplit(".", 1)[-1]
        ct = C.CT_RENAMED if leaf_s != leaf_t else C.CT_MAPPED
        detail_file = gdef.file
        key = (src_path, tgt_path, ct, detail_file)
        if key in self.records:
            return
        notes = f"translate={url}" if url else None
        self.records[key] = C.rec(
            C.SRC_FML, C.PAIR_R4B_R5, ct,
            earlier_path=src_path, later_path=tgt_path,
            raw_old=leaf_s, raw_new=leaf_t,
            relationship="fml",
            detail_file=detail_file, detail_ref=gname,
            notes=notes, structure=root,
        )
        if fallback:
            self.fallback_keys.add(key)

    def run_stmt(self, st, env, gdef, gname, root, depth, visited,
                 fallback, block):
        # resolve source
        src_base = env.get(st.src_var)
        src_path = None
        if src_base and src_base[0] == "source":
            src_path = src_base[1] + st.src_fields

        # resolve target segments sequentially (binding intermediate aliases)
        env2 = dict(env)
        if st.src_alias and src_path is not None:
            env2[st.src_alias] = ("source", src_path)

        primary_tgt_path = None
        for var, fields, alias in st.tgt_segs:
            base = env2.get(var)
            path = None
            if base and base[0] == "target":
                path = base[1] + fields
            if (st.tgt_primary is not None
                    and (var, fields) == st.tgt_primary
                    and primary_tgt_path is None
                    and path is not None):
                primary_tgt_path = path
            if alias:
                if path is not None:
                    env2[alias] = ("target", path)
                elif base is not None:
                    env2[alias] = ("target", base[1])

        # emit parent correspondence when both leaves resolved
        if (src_path is not None and st.src_fields
                and primary_tgt_path is not None):
            self.emit(src_path, primary_tgt_path, gdef, gname, root,
                      st.translate_url, fallback)

        # recurse into then-clause
        if st.then is None:
            return
        if st.then[0] == "inline":
            for sub in st.then[1]:
                self.run_stmt(sub, env2, gdef, gname, root, depth + 1,
                              visited, fallback, block)
            return

        # named group call
        _, sub_name, args = st.then
        sub = self.groups.get(sub_name)
        if sub is None:
            self.undefined.add(sub_name)
            return
        # Typed groups (datatypes/resources) are their own roots -> resolved
        # standalone elsewhere; do NOT expand them inline at the call site
        # (avoids combinatorial datatype blow-up + Reference/Identifier cycles).
        if sub.typed:
            return
        # self-recursion / cycle guard: never re-enter a backbone already on
        # this branch (e.g. CodeSystem.concept.concept...).
        if sub_name in visited:
            return
        if depth + 1 > MAX_DEPTH:
            return
        if block is not None and sub_name in block:
            return
        # fallback source/target paths (positional) if arg names don't resolve
        fb_src = src_path if src_path is not None else (
            src_base[1] if src_base else None)
        fb_tgt = primary_tgt_path
        if fb_tgt is None and st.tgt_segs:
            b = env.get(st.tgt_segs[0][0])
            if b and b[0] == "target":
                fb_tgt = b[1]
        sub_env = {}
        for i, (role, pname) in enumerate(sub.params):
            node = env2.get(args[i]) if i < len(args) else None
            if node is None:
                node = ("source", fb_src) if role == "source" else ("target", fb_tgt)
            sub_env[pname] = node
        self.resolve_group(sub_name, sub_env, root, depth + 1,
                           visited | {sub_name}, fallback, block)

    def resolve_group(self, gname, env, root, depth, visited, fallback, block):
        g = self.groups[gname]
        if not fallback:
            self.covered.add(gname)
        for st in g.stmts:
            self.run_stmt(st, env, g, gname, root, depth, visited,
                          fallback, block)


# --------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------
def main():
    groups, invoked, files = load_groups()

    # Roots = typed groups (resources + datatypes): each is its own root and is
    # resolved standalone (prefix = group name). Untyped backbone groups are
    # expanded inline via `then` from their owning root.
    roots = [name for name in groups if groups[name].typed]
    backbones = [name for name in groups if not groups[name].typed]

    res = Resolver(groups)

    # Root pass: full-path resolution from every typed group.
    for root in sorted(roots):
        g = groups[root]
        env0 = {}
        for role, var in g.params:
            env0[var] = ("source" if role == "source" else "target", root)
        res.resolve_group(root, env0, root, 0, frozenset({root}), False, None)

    covered_by_roots = set(res.covered)

    # Fallback pass: any group never reached from a root -> group-relative.
    fallback_groups = [g for g in groups if g not in covered_by_roots]
    for gname in sorted(fallback_groups):
        g = groups[gname]
        env0 = {}
        for role, var in g.params:
            env0[var] = ("source" if role == "source" else "target", gname)
        if not env0:  # safety for param-less groups
            env0 = {"src": ("source", gname), "tgt": ("target", gname)}
        res.resolve_group(gname, env0, gname, 0, frozenset({gname}), True,
                          covered_by_roots)

    records = list(res.records.values())
    C.write_records(C.SRC_FML, records)

    # ----------------------------------------------------------------------
    # Report
    # ----------------------------------------------------------------------
    import json

    by_ct = Counter(r["change_type"] for r in records)
    n_fallback = len(res.fallback_keys)

    print("\n================ FML EXTRACTOR REPORT ================")
    print(f"FML files parsed          : {len(files)}")
    print(f"groups parsed (total)     : {len(groups)}")
    print(f"  roots (typed datatype/resource): {len(roots)}")
    print(f"  backbone sub-groups (untyped)  : {len(backbones)}")
    print(f"    (of which `then`-invoked)    : "
          f"{len([b for b in backbones if b in invoked])}")
    print(f"undefined `then` targets  : {len(res.undefined)}"
          + (f" -> {sorted(res.undefined)}" if res.undefined else ""))
    print(f"total records             : {len(records)}")
    print(f"change_type counts        : {dict(by_ct)}")
    print(f"group-relative (fallback) : {n_fallback} records"
          f" from {len(fallback_groups)} unresolved groups")

    # Validation: Patient4Bto5 expectations
    print("\n---- Validation (Patient4Bto5.fml) ----")
    want = [
        ("Patient.name", "Patient.name"),
        ("Patient.contact.name", "Patient.contact.name"),
        ("Patient.link.type", "Patient.link.type"),
    ]
    idx = {(r["earlier_path"], r["later_path"]): r
           for r in records if r["detail_file"] == "Patient4Bto5.fml"}
    for e, l in want:
        r = idx.get((e, l))
        if r:
            note = f" ; notes={r['notes']}" if r["notes"] else ""
            print(f"  OK  {e} -> {l}  [{r['change_type']}]"
                  f" (group={r['detail_ref']}){note}")
        else:
            print(f"  MISSING  {e} -> {l}")

    print("\n---- 6 sample records ----")
    samples = []
    # include the 3 validation rows if present
    for e, l in want:
        if (e, l) in idx:
            samples.append(idx[(e, l)])
    for r in records:
        if len(samples) >= 6:
            break
        if r not in samples:
            samples.append(r)
    for r in samples[:6]:
        print(json.dumps(r, ensure_ascii=False))

    print("======================================================")


if __name__ == "__main__":
    main()
