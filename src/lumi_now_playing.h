/* SPDX-License-Identifier: MIT */
#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <lvgl.h>

#define LUMI_TEXT_W 206
#define LUMI_TITLE_H 24
#define LUMI_ARTIST_H 20

#define LUMI_TITLE_BITMAP_MAX_W 480
#define LUMI_ARTIST_BITMAP_MAX_W 360

#define LUMI_ARTWORK_W 76
#define LUMI_ARTWORK_H 76
#define LUMI_ARTWORK_BYTES (LUMI_ARTWORK_W * LUMI_ARTWORK_H)

#define LUMI_TITLE_BITMAP_MAX_BYTES     ((LUMI_TITLE_BITMAP_MAX_W * LUMI_TITLE_H + 7) / 8)
#define LUMI_ARTIST_BITMAP_MAX_BYTES     ((LUMI_ARTIST_BITMAP_MAX_W * LUMI_ARTIST_H + 7) / 8)

void lumi_now_playing_init(lv_obj_t *screen);
void lumi_now_playing_update(const char *source,
                             const char *title, const char *artist,
                             uint32_t position_ms, uint32_t duration_ms,
                             bool playing);
bool lumi_now_playing_is_active(void);
void lumi_now_playing_set_bitmap(bool title_bitmap, uint16_t width,
                                 const uint8_t *data, size_t len);
void lumi_now_playing_set_artwork(const uint8_t *data, size_t len);
void lumi_now_playing_user_activity(void);
void lumi_now_playing_clear(void);
