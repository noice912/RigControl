#!/usr/bin/env python3
"""Builds RigControl's graphics card table from a pci.ids file.

    python -I build-gpu-table.py <path to pci.ids> [--out <gpu-table.txt>] [--update-snapshot]

The pci.ids file comes from the PCI ID Project (https://pci-ids.ucw.cz/). It is read as plain text only.
Its SHA-256 must match the one recorded in snapshot.json next to this script; pass --update-snapshot to
record a newer file instead (then commit snapshot.json together with the new table).

What is kept: graphics devices of NVIDIA (10de), AMD/ATI (1002) and Intel (8086). pci.ids has no device
class per entry, so other devices of those vendors (sound, USB, chipset, network) are left out by name.
Each kept line becomes:  device id | chip | name | family | architecture
Only the Python standard library is used.
"""
import argparse
import hashlib
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_OUT = os.path.normpath(os.path.join(HERE, "..", "..", "src", "RigControl.Core", "Hardware", "gpu-table.txt"))
VENDORS = {"10de": "NVIDIA", "1002": "AMD", "8086": "Intel"}

# ---------------------------------------------------------------- which devices are graphics devices

NV_NOT = re.compile(
    r"Audio|USB|nForce|\bMCP\d|\bCK8|CK804|Ethernet|Bridge|SMBus|SATA|\bIDE\b|Memory Controller|LPC|Root Complex|Tegra|UCSI|"
    r"Serial|Modem|AC.97|\bMAC\b|RAID|Co-?processor|HPET|PCI Express|PCIe|\bISA\b|Mellanox|BlueField|ConnectX|Switch|I2C|"
    r"Root Port|\bRP x\d",
    re.I,
)
NV_NAME = re.compile(r"GeForce|Quadro|RTX|GTX|Tesla|NVS|TITAN|GRID|Riva|Vanta|TNT", re.I)
NV_CHIP = re.compile(r"^(NV\d|G[7-9]\d|GT2\d\d|GF1\d\d|GK\d|GM\d|GP1|GV1|TU1|GA1|AD1|GB[12]0\d|GH1|GR1|C[5-7]\d\b)")

AMD_NOT = re.compile(
    r"Audio|USB|Bridge|Xilleon|Theater|\bIXP|\bSB\d|\bSBx|SMBus|SATA|\bIDE\b|LPC|Ethernet|Modem|AC.97|Root Port|PCI Express|"
    r"Host|Serial|HPET|RAID|IOMMU|Switch|Upstream|Downstream|Port\b|TV Wonder|Tuner|HDTV|Root Complex|I2C|Co-?processor|"
    r"Controller|Dummy",
    re.I,
)

INTEL_IS = re.compile(r"Graphics|\bArc\b|\bIris\b|\bUHD\b|\bXe\b|\bDG[12]\b|\bGT[0-4][a-z]?\b|\bi740\b|\bi75[24]\b", re.I)
INTEL_NOT = re.compile(
    r"Audio|Host Bridge|Thunderbolt|PCIe|PCI Express|Root Port|Port\b|USB|Ethernet|SMBus|Sound|Memory Controller Hub", re.I
)


def is_graphics(vendor, text):
    if vendor == "10de":
        return not NV_NOT.search(text) and bool(NV_NAME.search(text) or NV_CHIP.search(text))
    if vendor == "1002":
        return not AMD_NOT.search(text)
    if vendor == "8086":
        return bool(INTEL_IS.search(text)) and not INTEL_NOT.search(text)
    return False


# ---------------------------------------------------------------- chip, name, family, architecture

MARKETING = re.compile(r"Graphics|GeForce|Quadro|Radeon|FirePro|FireGL|\bArc\b|\bIris\b|Tesla|Rage|Riva", re.I)


def split_name(text):
    """'GA104 [GeForce RTX 4070 Ti]' -> ('GA104', 'GeForce RTX 4070 Ti'). No bracket: a chip name only, unless the
    text itself reads like a product name ('Iris Plus Graphics G4 (Ice Lake)')."""
    m = re.match(r"^(.*?)\s*\[(.+)\]\s*(\(.*\))?\s*$", text)
    if m:
        chip, name = m.group(1).strip(), m.group(2).strip()
        if m.group(3):
            name += " " + m.group(3)
        return chip, name
    if MARKETING.search(text):
        return "", text.strip()
    return text.strip(), ""


def nvidia_family(chip, name):
    n = name
    m = re.search(r"GeForce RTX (\d\d)\d\d", n)
    if m:
        return "GeForce RTX %s series" % m.group(1)
    m = re.search(r"GeForce GTX? (1[06])\d\d", n)
    if m:
        return "GeForce GTX %s series" % m.group(1)
    if re.search(r"GeForce MX\d", n):
        return "GeForce MX"
    if re.search(r"TITAN", n):
        return "TITAN"
    m = re.search(r"GeForce (?:GTX|GTS|GT|G|RTX)? ?(\d)\d\d(?!\d)", n)
    if m:
        return "GeForce %s00 series" % m.group(1)
    if re.search(r"GeForce FX|GeForce PCX", n):
        return "GeForce FX series"
    m = re.search(r"GeForce(?: Go)? ?(\d)\d{3}", n)
    if m:
        return "GeForce %s series" % m.group(1)
    if "GeForce" in n:
        return "GeForce (older)"
    if re.search(r"RTX PRO", n):
        return "RTX PRO"
    if re.search(r"RTX \d+.*Ada", n):
        return "RTX Ada Generation"
    if re.search(r"RTX A\d", n):
        return "RTX A-series"
    if re.search(r"Quadro RTX", n):
        return "Quadro RTX"
    if re.search(r"Quadro|NVS", n):
        return "Quadro / NVS"
    if re.search(r"Tesla|GRID", n):
        return "Tesla / GRID"
    if re.search(r"Riva|Vanta|TNT", n):
        return "Riva"
    return ""


NV_ARCH = [
    (r"^GB2", "Blackwell"), (r"^AD1", "Ada Lovelace"), (r"^GA1", "Ampere"), (r"^TU1", "Turing"), (r"^GV1", "Volta"),
    (r"^GP1", "Pascal"), (r"^GM\d", "Maxwell"), (r"^GK\d", "Kepler"), (r"^GF1", "Fermi"), (r"^GH1", "Hopper"),
]


def amd_family(chip, name):
    n = name
    m = re.search(r"Radeon RX (\d)\d{3}", n)
    if m:
        return "Radeon RX %s000 series" % m.group(1)
    m = re.search(r"Radeon (?:Pro )?RX (\d)\d\d(?!\d)", n)
    if m:
        return "Radeon RX %s00 series" % m.group(1)
    if re.search(r"Vega (56|64)|RX Vega|Vega Frontier", n):
        return "Radeon RX Vega"
    if re.search(r"Radeon VII", n):
        return "Radeon VII"
    if re.search(r"Instinct", n):
        return "Instinct"
    if re.search(r"Radeon Pro|Radeon PRO", n):
        return "Radeon Pro"
    if re.search(r"FirePro|FireGL|FireMV|FireStream", n):
        return "FirePro / FireGL"
    m = re.search(r"Radeon R[579] M?(\d)\d\d", n)
    if m:
        return "Radeon R5/R7/R9 %s00 series" % m.group(1)
    m = re.search(r"Radeon HD (\d)\d{3}", n)
    if m:
        return "Radeon HD %s000 series" % m.group(1)
    if re.search(r"Graphics|Radeon \d{3}M\b|Radeon Vega \d|Radeon R\d\b", n):
        return "Radeon integrated graphics"
    if re.search(r"Radeon X\d", n):
        return "Radeon X series"
    m = re.search(r"Radeon (\d)\d{3}", n)
    if m:
        return "Radeon %s000 series" % m.group(1)
    if re.search(r"Rage|Mach", n):
        return "Rage / Mach"
    if "Radeon" in n:
        return "Radeon (other)"
    return ""


AMD_ARCH = [
    (r"^Navi 4", "RDNA 4"), (r"^Navi 3", "RDNA 3"), (r"^Navi 2", "RDNA 2"), (r"^Navi 1", "RDNA"),
    (r"^Vega", "Vega"), (r"^(Ellesmere|Baffin|Lexa|Polaris)", "Polaris"),
]


def intel_family(chip, name):
    n = name
    discrete = bool(re.match(r"^(DG2|Battlemage|Alchemist|BMG|ACM)", chip))
    if re.search(r"\bArc\b", n):
        if not discrete:
            return "Arc integrated graphics"
        m = re.search(r"Arc (Pro )?([AB])\d", n)
        if m:
            return "Arc %s%s-series" % (m.group(1) or "", m.group(2))
        return "Arc"
    if re.search(r"Iris Xe MAX", n):
        return "Iris Xe MAX"
    if re.search(r"Iris Xe", n):
        return "Iris Xe Graphics"
    if re.search(r"\bIris\b", n):
        return "Iris Graphics"
    if re.search(r"UHD Graphics", n):
        return "UHD Graphics"
    if re.search(r"HD Graphics", n):
        return "HD Graphics"
    return "Intel integrated graphics"


INTEL_ARCH = [(r"^Battlemage", "Battlemage"), (r"^DG2", "Alchemist"), (r"^DG1", "DG1")]


def arch(table, chip):
    for pattern, label in table:
        if re.match(pattern, chip):
            return label
    return ""


def describe(vendor, text):
    chip, name = split_name(text)
    if vendor == "10de":
        return chip, name, nvidia_family(chip, name), arch(NV_ARCH, chip)
    if vendor == "1002":
        return chip, name, amd_family(chip, name), arch(AMD_ARCH, chip)
    return chip, name, intel_family(chip, name), arch(INTEL_ARCH, chip)


# ---------------------------------------------------------------- reading pci.ids


def read_devices(path):
    """Returns (header facts, {vendor: [(device, text)]}). Stops at the device class list at the end of the file."""
    facts = {}
    devices = {v: [] for v in VENDORS}
    vendor = None
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if line.startswith("#"):
                m = re.match(r"#\s*(Version|Date):\s*(.+?)\s*$", line)
                if m and m.group(1).lower() not in facts:
                    facts[m.group(1).lower()] = m.group(2)
                continue
            if not line.strip():
                continue
            if line.startswith("C "):
                break
            if line.startswith("\t\t"):
                continue
            if line.startswith("\t"):
                m = re.match(r"^\t([0-9a-fA-F]{4})\s+(.+?)\s*$", line)
                if m and vendor in devices:
                    devices[vendor].append((m.group(1).lower(), m.group(2)))
                continue
            m = re.match(r"^([0-9a-fA-F]{4})\s", line)
            vendor = m.group(1).lower() if m else None
    return facts, devices


def clean(text):
    return re.sub(r"\s+", " ", text.replace("|", "/")).strip()


def main():
    ap = argparse.ArgumentParser(description="Builds RigControl's graphics card table from pci.ids.")
    ap.add_argument("pci_ids")
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--update-snapshot", action="store_true", help="record this pci.ids file as the new pinned snapshot")
    args = ap.parse_args()

    with open(args.pci_ids, "rb") as f:
        sha = hashlib.sha256(f.read()).hexdigest()
    facts, devices = read_devices(args.pci_ids)
    snapshot_path = os.path.join(HERE, "snapshot.json")
    with open(snapshot_path, encoding="utf-8") as f:
        snapshot = json.load(f)
    if args.update_snapshot:
        snapshot.update({"version": facts.get("version", "unknown"), "date": facts.get("date", "unknown"), "sha256": sha})
        with open(snapshot_path, "w", encoding="utf-8", newline="\n") as f:
            json.dump(snapshot, f, indent=2)
            f.write("\n")
    elif sha != snapshot["sha256"]:
        sys.exit(
            "This pci.ids file is not the pinned snapshot.\n  expected SHA-256 %s\n  this file        %s\n"
            "Use the pinned file, or pass --update-snapshot to move to this one." % (snapshot["sha256"], sha)
        )

    out = [
        "# RigControl graphics card table. Generated by tools/hardware-db/build-gpu-table.py: do not edit by hand.",
        "# Made from pci.ids, the PCI ID Project's list (https://pci-ids.ucw.cz/), maintained by Albert Pool,",
        "# Martin Mares and other volunteers. pci.ids can be distributed under either the GNU General Public License",
        "# (version 2 or higher) or the 3-clause BSD License; see PCI-IDS-NOTICE.txt next to this file.",
        "# Lines: device id|chip|name|family|architecture, under a '@vendor id|vendor name' line.",
        "!version %s" % snapshot["version"],
        "!date %s" % snapshot["date"],
        "!sha256 %s" % snapshot["sha256"],
    ]
    total = 0
    for vendor in ("10de", "1002", "8086"):
        rows = {}
        for device, text in devices[vendor]:
            if is_graphics(vendor, text):
                rows[device] = describe(vendor, text)
        out.append("@%s|%s" % (vendor, VENDORS[vendor]))
        for device in sorted(rows):
            out.append("|".join([device] + [clean(x) for x in rows[device]]))
        total += len(rows)
        print("%s %s: %d graphics devices" % (vendor, VENDORS[vendor], len(rows)))
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(out) + "\n")
    print("Wrote %d devices to %s" % (total, args.out))


if __name__ == "__main__":
    main()
