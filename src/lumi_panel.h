#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

int lumi_panel_init(void);
int lumi_panel_set_sleep(bool sleeping);

/* Prepare ST7789 for external VCC removal before nRF52840 System OFF. */
int lumi_panel_enter_deep_sleep(void);

int lumi_panel_set_backlight(bool enabled);
