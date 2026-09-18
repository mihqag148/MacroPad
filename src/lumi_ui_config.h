/* SPDX-License-Identifier: MIT */
#pragma once

#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>

enum lumi_screensaver_style {
    LUMI_SAVER_TAHOE = 0,
    LUMI_SAVER_MINIMAL = 1,
    LUMI_SAVER_OFF = 2,
};

#define LUMI_SAVER_FRAME_W 160
#define LUMI_SAVER_FRAME_H 86
#define LUMI_SAVER_FRAME_BYTES (LUMI_SAVER_FRAME_W * LUMI_SAVER_FRAME_H)
#define LUMI_SAVER_MAX_FRAMES 25

void lumi_ui_set_wallpaper(uint8_t r1, uint8_t g1, uint8_t b1,
                           uint8_t r2, uint8_t g2, uint8_t b2);
void lumi_ui_set_screensaver(bool enabled, uint8_t style,
                             uint32_t delay_seconds,
                             uint8_t r1, uint8_t g1, uint8_t b1,
                             uint8_t r2, uint8_t g2, uint8_t b2);
void lumi_ui_set_sleep_timeout(uint32_t seconds);
void lumi_ui_set_screensaver_delay(uint32_t seconds);
void lumi_ui_note_activity(void);
void lumi_ui_set_media_active(bool active);

void lumi_ui_saver_anim_begin(uint8_t frame_count, uint16_t frame_interval_ms);
void lumi_ui_saver_anim_frame(uint8_t index, const uint8_t *data, size_t len);
void lumi_ui_saver_anim_chunk(uint8_t index, uint16_t offset,
                              const uint8_t *data, size_t len);
void lumi_ui_saver_anim_end(void);
bool lumi_ui_saver_anim_is_valid(void);
void lumi_ui_saver_anim_clear(void);
