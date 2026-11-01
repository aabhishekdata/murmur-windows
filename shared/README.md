# Shared test vectors

Specifications that more than one implementation runs. Change these first, watch every
consumer go red, then make them green.

| File | Written by | Run by |
|---|---|---|
| `dictionary-test-vectors.json` | hand | Swift (`VectorTests`, from a copy in `Tests/`), C# (`Murmur.Dictionary.Tests`) |
| `text-pipeline-vectors.json` | `TextPipelineVectorExportTests` in `windows/tests/Murmur.Core.Tests` | C#; the planned Android/JVM port |

`text-pipeline-vectors.json` is generated. Don't edit it by hand: change the C# behaviour or the
cases in the exporter, then regenerate it from `windows/`:

```bash
MURMUR_UPDATE_PIPELINE_VECTORS=1 dotnet test tests/Murmur.Core.Tests --filter TextPipelineVectorExport
```

## Rules for consumers of `text-pipeline-vectors.json`

- **Skip a case whose `pass` you don't implement**, and report it as skipped rather than passed.
- **Ignore fields you don't know.** With kotlinx.serialization that means
  `Json { ignoreUnknownKeys = true }`; with Jackson, `FAIL_ON_UNKNOWN_PROPERTIES = false`.
- **Treat a missing field as its default**: `false`, `null`, an empty list, or `"inline"` for
  a snippet's `match`. Fields are only written when they differ from the default.
- `version` only increases, and only when cases or fields are added. A newer version never
  changes what an older case means.

## Versions

| Version | Added |
|---|---|
| 1 | The six passes (`dictionary`, `filler`, `selfCorrection`, `command`, `snippet`, `list`) and `pipeline`. |
| 2 | Saved prompts. The `wholeUtterance` pass; `match` on a snippet (`"wholeUtterance"`; absent means inline); `wholeSnippet` and `verbatim` on a `pipeline` result. A `snippet` or `pipeline` case whose snippets include a whole-utterance prompt depends on that behaviour, so an implementation without it should skip any case with `"match": "wholeUtterance"` in its snippets rather than fail it. |
