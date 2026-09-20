#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

int lumi_panel_init(void);
int lumi_panel_set_sleep(bool sleeping);

int lumi_panel_set_backlight(bool enabled);
