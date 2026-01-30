# Binlog MCP Tool for AI Assistants

This repository is configured to use the [baronfel/mcp-binlog-tool](https://github.com/baronfel/mcp-binlog-tool) MCP (Model Context Protocol) server. This enables AI assistants like GitHub Copilot (in VS Code and CLI) to analyze MSBuild binary log files (`.binlog`).

## Setup

### One-time Installation

Install the tool globally (requires .NET SDK):

```bash
dotnet tool install --global baronfel.binlog.mcp --version 0.0.12 --add-source https://api.nuget.org/v3/index.json
```

> **Note**: The `--add-source` flag is required because the MSBuild repository's NuGet.config doesn't include nuget.org.

### Verification

After installation, verify the tool is available:

```bash
binlog.mcp --version
```

## Usage

Once installed, the MCP server is automatically available to:
- **VS Code** with GitHub Copilot (via `.vscode/mcp.json`)
- **GitHub Copilot CLI** (via `.copilot/mcp.json`)

### Available Tools

The binlog MCP server provides tools for analyzing `.binlog` files:

| Tool | Description |
|------|-------------|
| `load_binlog` | Load a binlog file for analysis |
| `get_diagnostics` | Extract errors and warnings |
| `get_expensive_analyzers` | Find slow Roslyn analyzers |
| `get_expensive_projects` | Find projects with longest build times |
| `list_projects` | List all projects in the build |
| `list_evaluations` | List project evaluations |
| `search_binlog` | Powerful freetext search using MSBuild Structured Log Viewer syntax |

### Example Prompts

After running a build with `-bl` to generate a binlog:

- "Load the msbuild.binlog file and show me the most expensive analyzers"
- "What errors occurred in the build?"
- "Which projects took the longest to build?"
- "Search for uses of the Copy task"

## Updating the Tool

To update to a newer version:

```bash
dotnet tool update --global baronfel.binlog.mcp --add-source https://api.nuget.org/v3/index.json
```

## More Information

- [mcp-binlog-tool GitHub repository](https://github.com/baronfel/mcp-binlog-tool)
- [NuGet package](https://www.nuget.org/packages/baronfel.binlog.mcp)
- [Binary Log documentation](wiki/Binary-Log.md)
