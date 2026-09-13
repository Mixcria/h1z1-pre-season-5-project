# tools/client — added client-side files

Files this project adds to the client tree. They are **added**, never patched into an existing
client file and never repacked: deleting one restores the client exactly.

| file | installed at | added by |
| --- | --- | --- |
| `CranberryMenu.lua` | `C:\Aug2017\Client\Resources\Scripts\CranberryMenu.lua` | D210, docs/103 §12 |

`CranberryMenu.lua` is the client half of the graphical mod menu. There is no auto-load: the
client never scans `Resources\Scripts\`, so the file is loaded by typing its bootstrap line into
the client's own debug console (docs/103 §12.1). The copy here is the source of truth; the copy
under `Client\Resources\Scripts` is the installed one.
