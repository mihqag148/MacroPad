/* SPDX-License-Identifier: MIT */
#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <lvgl.h>

#define LUMI_TEXT_W 206
#define LUMI_TITLE_H 24
#define LUMI_ARTIST_H 20
#define LUMI_TITLE_BITMAP_BYTES ((LUMI_TEXT_W * LUMI_TITLE_H + 7) / 8)
#define LUMI_ARTIST_BITMAP_BYTES ((LUMI_TEXT_W * LUMI_ARTIST_H + 7) / 8)

void lumi_now_playing_init(lv_obj_t *screen);
void lumi_now_playing_update(const char *title, const char *artist,
                             uint32_t position_ms, uint32_t duration_ms,
                             bool playing);
void lumi_now_playing_set_bitmap(bool title_bitmap,
                                 const uint8_t *data, size_t len);
