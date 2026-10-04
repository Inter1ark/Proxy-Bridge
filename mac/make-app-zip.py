#!/usr/bin/env python3
"""Zip a .app bundle, marking the executables inside as 0755.

Windows cannot store Unix permission bits, so a plain zip would produce an
app that macOS refuses to launch. This script writes the external attributes
explicitly so that "unzip" / Archive Utility on macOS restore the modes.

usage: make-app-zip.py <bundle.app> <out.zip> [exec-name ...] [--extra <file> ...]
Extra files are added next to the bundle at the zip root (scripts get 0755).
"""
import os
import stat
import sys
import zipfile


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    bundle = os.path.abspath(sys.argv[1])
    out = os.path.abspath(sys.argv[2])
    rest = sys.argv[3:]
    extras = []
    names = []
    i = 0
    while i < len(rest):
        if rest[i] == "--extra" and i + 1 < len(rest):
            extras.append(os.path.abspath(rest[i + 1]))
            i += 2
        else:
            names.append(rest[i])
            i += 1
    exec_names = set(names) or {"ProxyBridge", "pbcore"}
    if not os.path.isdir(bundle):
        print(f"not a directory: {bundle}")
        return 1

    parent = os.path.dirname(bundle)
    count = 0
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for root, dirs, files in os.walk(bundle):
            dirs.sort()
            # Directory entries so that empty folders survive and the tree is explicit.
            rel_dir = os.path.relpath(root, parent).replace(os.sep, "/") + "/"
            info = zipfile.ZipInfo(rel_dir)
            info.external_attr = (stat.S_IFDIR | 0o755) << 16
            info.external_attr |= 0x10  # MS-DOS directory flag
            zf.writestr(info, b"")
            for name in sorted(files):
                full = os.path.join(root, name)
                rel = os.path.relpath(full, parent).replace(os.sep, "/")
                in_macos = "/Contents/MacOS/" in "/" + rel
                executable = (
                    name in exec_names
                    or name.endswith(".dylib")
                    or (in_macos and "." not in name)
                )
                mode = 0o755 if executable else 0o644
                info = zipfile.ZipInfo.from_file(full, rel)
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = (stat.S_IFREG | mode) << 16
                with open(full, "rb") as f:
                    zf.writestr(info, f.read())
                count += 1
        for extra in extras:
            name = os.path.basename(extra)
            info = zipfile.ZipInfo.from_file(extra, name)
            info.compress_type = zipfile.ZIP_DEFLATED
            mode = 0o755 if name.endswith(".sh") else 0o644
            info.external_attr = (stat.S_IFREG | mode) << 16
            with open(extra, "rb") as f:
                zf.writestr(info, f.read())
            count += 1
    print(f"{out}: {count} files")
    return 0


if __name__ == "__main__":
    sys.exit(main())
