#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

int lumi_panel_init(void);
int lumi_panel_set_sleep(bool sleeping);
int lumi_panel_enable_async_flush(void);
int lumi_panel_render_rgb332_scaled(const uint8_t *src,
                                    uint16_t src_w,
                                    uint16_t src_h);
