#!/bin/sh

echo "Deleting old archives"
rm win-x64.zip
rm linux-x64.tar.gz

echo "Packing win-x64 build"
zip -r win-x64.zip win-x64

echo "Packing linux-x64 build"
chmod +x linux-x64/Loader
chmod -R +x linux-x64/tools/
tar cvzf linux-x64.tar.gz linux-x64

echo "Packing linux-x64 build"
chmod +x osx-x64/Loader
chmod -R +x osx-x64/tools/
zip -r osx-x64.zip osx-x64