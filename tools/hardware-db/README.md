# Hardware database

RigControl carries two small tables so it can say what a PC's processor and graphics card are. "Known" always means
known to the data in this version: a newer part shows the name it reports itself.

## Graphics cards (generated)

`src/RigControl.Core/Hardware/gpu-table.txt` is made from `pci.ids`, the list kept by the PCI ID Project
(https://pci-ids.ucw.cz/). It holds the graphics devices of NVIDIA (10de), AMD/ATI (1002) and Intel (8086):
device id, chip name, product name, family and architecture. It is built into `RigControl.Core.dll`
(`Hardware/Hardware.targets`) and read by `GpuDatabase`.

The pinned snapshot is recorded in `snapshot.json` (version, date, SHA-256). The pci.ids file itself is not kept in
this repository. To make the table again:

    python -I tools/hardware-db/build-gpu-table.py <path to pci.ids>

The script refuses a file whose SHA-256 is not the pinned one. To move to a newer pci.ids:

    python -I tools/hardware-db/build-gpu-table.py <path to newer pci.ids> --update-snapshot

then update the snapshot lines in `src/RigControl.Core/Hardware/PCI-IDS-NOTICE.txt`, run the tests in
`tests/RigControl.Core.Tests/Hardware`, and commit the table, `snapshot.json` and the notice together.

The script reads pci.ids as plain text and uses only the Python standard library. pci.ids has no device class per
entry, so sound, USB, chipset and network devices of the three vendors are left out by their names (the patterns
are at the top of the script). Family and architecture are worked out from the product and chip names; where a name
does not say, the field is left empty rather than guessed.

Licence: pci.ids can be distributed under either the GNU General Public License (version 2 or higher) or the
3-clause BSD License (SPDX: `GPL-2.0-or-later OR BSD-3-Clause`). The full notice is in
`src/RigControl.Core/Hardware/PCI-IDS-NOTICE.txt` and must stay next to the table.

## Processors (written by hand)

No complete free list of every processor model exists, and none is needed: every processor reports its own exact
name. `src/RigControl.Core/Hardware/CpuDatabase.cs` only adds the generation and code name, looked up by what the
processor says through CPUID: vendor, family, model and, where one model number covers several generations, stepping.

Where the entries come from:

- Intel family 6 model numbers: the model list in the Linux kernel (`arch/x86/include/asm/intel-family.h`), checked
  against Intel's own processor signatures in its microcode update guidance. The steppings that tell Skylake-X,
  Cascade Lake and Cooper Lake apart (model 0x55), Kaby Lake, Coffee Lake, Whiskey Lake and Comet Lake apart
  (0x8E, 0x9E) come from the same guidance.
- AMD families 17h, 19h and 1Ah: the Zen generation by model range follows the Linux kernel
  (`arch/x86/kernel/cpu/amd.c`); code names for single models follow AMD's product documentation and the kernel's
  comments.
- The entries were written from knowledge of those sources as of mid 2026. None of them has been checked by running
  RigControl on that processor yet, and the AMD side has not been tried on a real AMD PC at all.

Rules for this table:

- Only add an entry you are sure of. A missing entry shows the processor's own name with "newer than this version
  knows"; a wrong entry misleads.
- One model number sold as two generations (Raptor Lake is both 13th and 14th gen) lists the range, and the
  generation is then read from the model number in the processor's own name when it falls inside that range.
- AMD: put exact models before the wide ranges of the same family. The first row that fits wins.
- Add a test line in `tests/RigControl.Core.Tests/Hardware/CpuDatabaseTests.cs` for every new entry.

Not in the table on purpose: Intel before Sandy Bridge and the Atoms before Apollo Lake (named one by one in
`IntelOlderModels`, because Intel's model numbers do not follow age), the Pentium 4 and older, AMD before Zen (all
shown as "older than this version's list goes"), Zen 6 (model ranges are reserved but no product names are certain yet), and other makers.
