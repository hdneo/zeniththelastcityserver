# IL2CPP analysis scripts (Python 3.10, no extra packages)

These scripts were used to extract the protocol, opcodes, components and server URLs from the client.

| Script | Purpose |
|---|---|
| `meta.py OUT_types.txt OUT_strings.txt` | read global-metadata.dat (v29): all types/fields/methods + string literals |
| `binmap.py out/methods.tsv` | method -> address (RVA) in GameAssembly.dll (via CodeGenModules) – **run this first** |
| `il2.py "Namespace.Class::Method"` | annotated disassembly (objdump) with names for calls, strings, types, fields |
| `dis.sh ...` | like il2.py, more compact |
| `enums.py "ClassName"` | fields + constant values (enums, const strings) |
| `xref.py "Regex"` | who calls a method? |
| `schema.py` | raw field IDs of all components -> out/component_schemas.txt |
| `compsets.py` | component sets -> out/component_sets.txt |

Environment variables: `ZENITH_GAME_DIR` (game folder, e.g. `C:/Program Files (x86)/Steam/steamapps/common/Zenith MMO`), `ZENITH_OUT` (output, default `./out`), `OBJDUMP` (path to objdump.exe, e.g. from CLion/MinGW).
Always run with `python -I` from this folder.

Note: some console output and comments in these scripts are still in German.
