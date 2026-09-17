/* SPDX-License-Identifier: MIT */
#pragma once

#include <stdbool.h>
#include <stdint.h>
#include <lvgl.h>

void lumi_now_playing_init(lv_obj_t *screen);
void lumi_now_playing_update(const char *title, const char *artist,
                             uint32_t position_ms, uint32_t duration_ms,
                             bool playing);
