#pragma once
#include <stddef.h>
#include <stdint.h>

int lumi_panel_init(void);
int lumi_panel_render_rgb332_scaled(const uint8_t *src,
                                    uint16_t src_w,
                                    uint16_t src_h);
