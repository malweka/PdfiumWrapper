#!/bin/bash
cd /Users/hamsman/Dev/PdfiumWrapper/src
echo "Running PdfFormTests..."
dotnet test --filter "FullyQualifiedName~PdfFormTests" --logger "console;verbosity=normal"
echo "Exit code: $?"

