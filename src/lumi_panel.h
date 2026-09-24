#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

int lumi_panel_init(void);
int lumi_panel_set_sleep(bool sleeping);

/* Deep sleep is different from normal BLE eco sleep: put the ST7789
 * controller itself into SLPIN before ZMK cuts external VCC and enters
 * nRF52840 System OFF. Wake from this path is a full boot.
 */
int lumi_panel_enter_deep_sleep(void);

int lumi_panel_set_backlight(bool enabled);
