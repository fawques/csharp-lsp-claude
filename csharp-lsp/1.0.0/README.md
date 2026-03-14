# csharp-lsp

C# language server plugin for Claude Code, with a warmup wrapper that eliminates cold-start blocking.

## The problem

`csharp-ls` has significant cold-start time — it needs to load the .NET solution, resolve types, and build a semantic model. In Claude Code, the first LSP tool call blocks the model while `csharp-ls` loads, often timing out.

## How it works

Instead of calling `csharp-ls` directly, this plugin wraps it with a proxy (`csharp-ls-wrapper.cs`) that:

1. Starts `csharp-ls` as a child process and proxies all LSP messages over stdio
2. Lets the `initialize` handshake through normally (protocol requirement)
3. On the **first real request** (e.g. `findReferences`), races against a 3-second timeout — if `csharp-ls` is still loading, returns an empty result immediately
4. `csharp-ls` stays alive and finishes loading in the background
5. All subsequent requests are proxied normally to the now-warm server

The wrapper is a single-file C# app that runs via `dotnet run` (.NET 10+).

## Requirements

- .NET SDK 10.0 or later (for single-file `dotnet run` support)
- `csharp-ls` installed and on PATH

### Installing csharp-ls

```bash
# Via .NET tool (recommended)
dotnet tool install --global csharp-ls

# Via Homebrew (macOS)
brew install csharp-ls
```

## Installation

Enable the plugin in your Claude Code settings (`~/.claude/settings.json`):

```json
{
  "enabledPlugins": {
    "csharp-lsp@your-marketplace": true
  }
}
```

## Recommended: session-start hook

To trigger the warmup early, add a `UserPromptSubmit` hook that reminds Claude to fire an LSP call on the first message. Add to your project's `.claude/settings.json`:

```json
{
  "hooks": {
    "UserPromptSubmit": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "f=\"/tmp/.claude-lsp-warmup-$PPID\"; if [ ! -f \"$f\" ]; then touch \"$f\"; echo 'SESSION START: Fire an LSP findReferences call to warm up csharp-ls. It will return fast thanks to the wrapper.'; fi",
            "timeout": 5
          }
        ]
      }
    ]
  }
}
```

## More Information

- [csharp-ls GitHub](https://github.com/razzmatazz/csharp-language-server)
- [.NET SDK Download](https://dotnet.microsoft.com/download)
- [dotnet run with single files](https://devblogs.microsoft.com/dotnet/announcing-dotnet-run-app/)
