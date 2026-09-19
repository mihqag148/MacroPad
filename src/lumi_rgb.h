/* SPDX-License-Identifier: MIT */
#pragma once

#include <stdbool.h>
#include <stdint.h>

enum lumi_rgb_effect_id {
    LUMI_RGB_EFFECT_RAINBOW = 0,
    LUMI_RGB_EFFECT_PURPLE_PINGPONG = 1,
    LUMI_RGB_EFFECT_ORANGE_BLINK = 2,
    LUMI_RGB_EFFECT_SOLID = 3,
    LUMI_RGB_EFFECT_REACTIVE = 4,
    LUMI_RGB_EFFECT_COUNT = 5,
};

void lumi_rgb_set_enabled(bool enabled);
void lumi_rgb_set_brightness_percent(uint8_t percent);
void lumi_rgb_set_speed_percent(uint8_t percent);
void lumi_rgb_set_auto(bool enabled);
void lumi_rgb_set_effect(uint8_t effect);
void lumi_rgb_set_solid(uint8_t r, uint8_t g, uint8_t b);
void lumi_rgb_set_pixel_mode(bool enabled);
void lumi_rgb_set_pixel(uint8_t index, uint8_t r, uint8_t g, uint8_t b);
void lumi_rgb_set_profile(uint8_t index, uint8_t effect,
                          uint8_t r, uint8_t g, uint8_t b);
void lumi_rgb_set_profile_pixel_mode(uint8_t profile, bool enabled);
void lumi_rgb_set_profile_pixel(uint8_t profile, uint8_t index,
                                uint8_t r, uint8_t g, uint8_t b);
void lumi_rgb_set_suspended(bool suspended);
