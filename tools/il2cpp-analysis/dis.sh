#!/bin/bash
# compact annotated disassembly
cd "$(dirname "$0")"
python -I il2.py "$@" | grep -v 'int3$' | sed 's/^ *\([0-9a-f]*\):\t/\1  /' | grep -vE 'call   0x1808ff5c0|mov    BYTE PTR \[rip\+0x[0-9a-f]+\],0x1 |cmp    BYTE PTR \[rip\+0x[0-9a-f]+\],0x0 '
