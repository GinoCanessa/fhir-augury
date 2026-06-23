# Dictionary source files

These files (`*.words.txt` word lists and `*.typo.txt` typo→correction maps)
are the editable source for the spell-check database the application reads,
`cache/dictionary.db`. Edits here **do not take effect** until that database is
rebuilt.

After editing anything in this folder, rebuild from the repository root:

```sh
dotnet run --project tools/dictionary-build
```

`cache/dictionary.db` is gitignored, so each contributor rebuilds it locally.
See [`tools/dictionary-build/README.md`](../tools/dictionary-build/README.md)
for flags and details.
