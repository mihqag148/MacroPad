# Lumi QMK Raw HID bridge

This folder is the firmware-side companion for the Lumi Macropad Windows app.

QMK Raw HID uses fixed 32-byte reports. The Lumi bridge adds fragmentation so
the app can send the same line-oriented product commands used by the other
Lumi firmware drivers.

## QMK setup

In the QMK keymap or userspace `rules.mk`:

```make
RAW_ENABLE = yes
SRC += lumi_raw_hid.c
```

Copy `lumi_raw_hid.c` and `lumi_raw_hid.h` beside the keymap/userspace
source.

Optional `config.h` capability declaration:

```c
#define LUMI_QMK_CAPS "PROFILE,ACTION,PCMON,BAT"
```

Keep the QMK default Raw HID usage page and usage ID unless the app product
definition is changed to match:

```c
#define RAW_USAGE_PAGE 0xFF60
#define RAW_USAGE_ID   0x61
```

QMK currently uses `RAW_EPSIZE == 32`; the bridge deliberately fails the
build if a different Raw HID endpoint size is selected.

## Product command callback

Implement this in the keymap/userspace:

```c
#include "lumi_raw_hid.h"

bool lumi_qmk_process_command(
    const char *command,
    char *response,
    size_t response_size) {

    if (strcmp(command, "BAT") == 0) {
        snprintf(response, response_size, "BAT|100");
        return true;
    }

    if (strncmp(command, "RGB|EN|", 7) == 0) {
        // Apply product-specific RGB state.
        return false;
    }

    if (strncmp(command, "PCMON|", 6) == 0) {
        // Parse PC Monitor telemetry and update the product display.
        return false;
    }

    return false;
}
```

The bridge itself handles the `HELLO` handshake and returns:

```text
LUMIPAD|3|FW=QMK|CAPS=<LUMI_QMK_CAPS>
```

Only advertise capabilities the product actually implements.

## App product registration

Every real QMK product should use a unique USB VID/PID pair in its QMK
`info.json`/keyboard definition and register those IDs in
[`ProductCatalog.cs`](https://github.com/mihqag148/Lumipad-APP/blob/main/pc-app/LumiPad.App/ProductCatalog.cs). The Windows app probes only that VID/PID and then
requires the Lumi `HELLO` handshake, so an unrelated HID interface is not
accepted as a Lumi device.
