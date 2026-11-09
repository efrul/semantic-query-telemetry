#!/bin/sh
# The core's tests on Linux, with Mono (apt install mono-mcs mono-runtime): compiles src/Core and tests/ as C# 5, the
# language of the compiler that comes with Windows, and runs them. SQLite is the system's libsqlite3, mapped in for
# Windows' winsqlite3.dll.
set -e
root=$(cd "$(dirname "$0")/.." && pwd)
out=$(mktemp -d)
mcs -langversion:5 -nologo -warnaserror+ -nowarn:1591 -out:"$out/tests.exe" -r:System.dll -r:System.Core.dll \
    "$root"/src/Core/*.cs "$root"/tests/*.cs
cat > "$out/tests.exe.config" <<'XML'
<configuration><dllmap dll="winsqlite3.dll" target="libsqlite3.so.0"/></configuration>
XML
mono "$out/tests.exe" "$out/work"
