# Contributing

## Getting set up

```bash
dotnet restore
dotnet build
dotnet test
```

The test suite needs no network access, no Azure credentials and no OpenSearch cluster.

## Before opening a pull request

```bash
dotnet format                     # applies the .editorconfig rules
dotnet build                      # warnings are errors
dotnet test
```

CI runs the same three steps, plus CodeQL and a container build.

## House rules

- **Warnings are errors.** If an analyzer fires, fix the cause rather than suppressing it. A
  suppression needs a `Justification` that says why the rule does not apply here.
- **The analyzer band is pinned** (`AnalysisLevel` in `Directory.Build.props`) and the SDK band
  is pinned in `global.json`. Combined with warnings-as-errors, a floating rule set would let a
  green build turn red on an SDK patch bump with no code change. Raise the level deliberately,
  in its own commit, and fix what it surfaces.
- **New behaviour comes with tests.** Anything touching authorization, argument validation,
  identity resolution or query construction needs tests for the failure paths too, not only
  the happy path.
- **Fail closed.** A missing configuration value, an unknown identity, or an unparseable
  registry must stop the gateway or deny the call — never fall back to permissive behaviour.
- **Nothing sensitive reaches the caller.** Backend errors, connection strings and stack
  traces go to the logs; the model gets a short message or a correlation id.
- **Log through `Diagnostics/Log.cs`.** Source-generated messages keep event ids in one place
  and allocate nothing when the level is disabled.

## Adding a connector

See the "Extending" section of the README. A connector needs:

1. A class deriving from `Connector`.
2. Tools returning `ToolDefinition`s with a JSON Schema for their inputs.
3. An entry in `ConnectorFactory`'s factory map.
4. Tests covering its options validation and its tools' argument handling, using a fake
   transport rather than a live backend — `RecordingConnection` and `FakeOpenSearchBackend`
   show the pattern.
