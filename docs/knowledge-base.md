# Knowledge base

RecompRadar recognises well-known API, library and device names with a JSON knowledge base. The built-in file is
`src/RecompRadar.BinaryAnalysis/Knowledge/builtin.json` (embedded in the assembly). Additional `*.json` files are merged
in from the *knowledge folder* (`%LOCALAPPDATA%\RecompRadar\knowledge` by default; a different folder can be chosen in
Settings).

The knowledge base only contains **names that exist in public SDKs, libraries and documentation**. A match means "this
name is present in the file". It never means "the program calls this at run time" – the analyzer does not follow code.
Platform associations are hints and are reported as such.

## File format

```json
{
  "schemaVersion": 1,
  "name": "My extra rules",
  "version": "1",
  "rules": [
    {
      "id": "acme.engine",
      "kind": "symbol",
      "match": "prefix",
      "patterns": ["acme_", "AcmeEngine"],
      "category": "Middleware",
      "apiFamily": "Acme game engine",
      "platforms": ["ps2"],
      "hardware": false,
      "description": "Functions of the (fictional) Acme engine."
    }
  ]
}
```

| Field | Meaning |
| --- | --- |
| `id` | Unique, stable id of the rule (required). Reported as `RuleId`. |
| `kind` | `symbol` (imported or defined function names), `library` (needed shared libraries, case-insensitive) or `string` (printable strings in non-executable data sections). |
| `match` | `exact` (default), `prefix` or `contains`. |
| `patterns` | One or more strings to match (required, up to 256 characters each). |
| `category` | Group shown in the UI, for example `Graphics`, `Audio`, `Console SDK`, `Hardware access`. |
| `apiFamily` | Name of the API or library family. |
| `platforms` | Platform ids this name hints at: `ps1`, `ps2`, `ps3`, `ps4`, `psp`, `vita`, `gamecube`, `wii`, `linux`. Names that exist on several platforms should list all of them. A name listed for three or more platforms only counts as a weak hint. |
| `hardware` | `true` reports matches under *Hardware hints* instead of *Known APIs*. |
| `description` | Short explanation shown to the user. |

## Safety and limits

* Files larger than 5 MiB, more than 50 files, or more than 20 000 rules in total are not loaded.
* Invalid JSON, rules without an id or pattern, and a missing folder are reported as notes on the *Dependencies* page
  (tab *Notes*); the built-in rules keep working.
* Matching is exact string comparison; there is no regular-expression engine, so a rule file cannot slow the analysis down.

## Adding names responsibly

Only add names you can point to in public documentation or source code of the SDK or library in question. Do not add
guesses, and do not copy documentation text: a name and a one-line description are enough.
