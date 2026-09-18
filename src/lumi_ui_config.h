/* SPDX-License-Identifier: MIT */
#pragma once

#include <stdbool.h>
#include <stdint.h>

enum lumi_screensaver_style {
    LUMI_SAVER_TAHOE = 0,
    LUMI_SAVER_MINIMAL = 1,
    LUMI_SAVER_OFF = 2,
};

void lumi_ui_set_wallpaper(uint8_t r1, uint8_t g1, uint8_t b1,
                           uint8_t r2, uint8_t g2, uint8_t b2);
void lumi_ui_set_screensaver(bool enabled, uint8_t style,
                             uint32_t delay_seconds,
                             uint8_t r1, uint8_t g1, uint8_t b1,
                             uint8_t r2, uint8_t g2, uint8_t b2);
void lumi_ui_set_sleep_timeout(uint32_t seconds);
void lumi_ui_note_activity(void);
