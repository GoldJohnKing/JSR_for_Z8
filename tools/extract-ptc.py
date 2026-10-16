#!/usr/bin/env python3
"""Extract the Nikon Z8 rows from the reference PTC index into the embedded resource.

Reads reference/Jiangtherapee-Bridge-2.3.27-Windows-x64/resources/_internal/ptc_index.csv,
keeps the rows with brand==Nikon and model==Z8 (12 ISO entries), and writes them with the
original column set to src/Jsr4Z8.Core/Ptc/Embedded/ptc_z8.csv, which Jsr4Z8.Core embeds
(LogicalName Jsr4Z8.Core.Ptc.Embedded.ptc_z8.csv).

The repo copy of the source CSV has no BOM and no spaces after commas, but the extractor
strips both defensively in case the file is regenerated from the original distribution.

Usage: python3 tools/extract-ptc.py   (paths resolve relative to the repo root)
"""

from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SRC = REPO / "reference/Jiangtherapee-Bridge-2.3.27-Windows-x64/resources/_internal/ptc_index.csv"
DST = REPO / "src/Jsr4Z8.Core/Ptc/Embedded/ptc_z8.csv"
BRAND, MODEL = "Nikon", "Z8"


def split_fields(line: str) -> list[str]:
    return [field.strip() for field in line.strip().lstrip("\ufeff").split(",")]


def main() -> None:
    header: list[str] | None = None
    rows: list[list[str]] = []
    for line in SRC.read_text(encoding="utf-8-sig").splitlines():
        if not line.strip():
            continue
        fields = split_fields(line)
        if header is None:
            header = fields
            continue
        if fields[header.index("brand")] == BRAND and fields[header.index("model")] == MODEL:
            rows.append(fields)

    assert header is not None, "empty source CSV"
    rows.sort(key=lambda f: float(f[header.index("iso_nominal")]))

    DST.parent.mkdir(parents=True, exist_ok=True)
    with DST.open("w", encoding="utf-8", newline="\n") as out:
        out.write(",".join(header) + "\n")
        for fields in rows:
            out.write(",".join(fields) + "\n")

    first = rows[0][header.index("iso_nominal")]
    last = rows[-1][header.index("iso_nominal")]
    print(f"{DST.relative_to(REPO)}: {len(rows)} {BRAND} {MODEL} rows (ISO {first}..{last})")


if __name__ == "__main__":
    main()
