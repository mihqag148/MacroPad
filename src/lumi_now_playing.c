/* SPDX-License-Identifier: MIT */

#include <stdio.h>
#include <string.h>
#include <zephyr/kernel.h>
#include <lvgl.h>
#include <zmk/display.h>

#include "lumi_now_playing.h"

#define MUSIC_TIMEOUT_MS 4500

struct music_state {
    char title[64];
    char artist[48];
    uint32_t position_ms;
    uint32_t duration_ms;
    bool playing;
    uint32_t last_rx_ms;

    uint8_t title_bitmap[LUMI_TITLE_BITMAP_BYTES];
    uint8_t artist_bitmap[LUMI_ARTIST_BITMAP_BYTES];
    bool title_bitmap_valid;
    bool artist_bitmap_valid;
};

static struct music_state state;
K_MUTEX_DEFINE(state_lock);

static lv_obj_t *page;
static lv_obj_t *album_card;
static lv_obj_t *album_icon;
static lv_obj_t *title_label;
static lv_obj_t *artist_label;
static lv_obj_t *title_canvas;
static lv_obj_t *artist_canvas;
static lv_obj_t *progress;
static lv_obj_t *elapsed_label;
static lv_obj_t *remain_label;
static lv_obj_t *play_label;

static lv_color_t title_canvas_buf[LUMI_TEXT_W * LUMI_TITLE_H];
static lv_color_t artist_canvas_buf[LUMI_TEXT_W * LUMI_ARTIST_H];

static bool ui_ready;

static void fmt_time(char *out, size_t len, uint32_t ms) {
    uint32_t s = ms / 1000U;
    snprintf(out, len, "%u:%02u", (unsigned)(s / 60U), (unsigned)(s % 60U));
}

static void draw_1bit(lv_obj_t *canvas, lv_color_t *dst,
                      const uint8_t *bits, size_t bit_len,
                      int width, int height, uint32_t fg_hex) {
    lv_color_t fg = lv_color_hex(fg_hex);
    lv_color_t bg = lv_color_hex(0x000000);
    size_t px_count = (size_t)width * height;

    for (size_t i = 0; i < px_count; i++) {
        size_t byte_i = i >> 3;
        uint8_t mask = (uint8_t)(0x80U >> (i & 7U));
        bool on = byte_i < bit_len && (bits[byte_i] & mask);
        dst[i] = on ? fg : bg;
    }

    lv_obj_invalidate(canvas);
}

static void apply_music_state(struct k_work *work);
K_WORK_DEFINE(music_work, apply_music_state);

static void apply_music_state(struct k_work *work) {
    ARG_UNUSED(work);

    if (!ui_ready) {
        return;
    }

    k_mutex_lock(&state_lock, K_FOREVER);

    if (state.title_bitmap_valid) {
        draw_1bit(title_canvas, title_canvas_buf,
                  state.title_bitmap, sizeof(state.title_bitmap),
                  LUMI_TEXT_W, LUMI_TITLE_H, 0xFFFFFF);
        lv_obj_add_flag(title_label, LV_OBJ_FLAG_HIDDEN);
        lv_obj_clear_flag(title_canvas, LV_OBJ_FLAG_HIDDEN);
    } else {
        lv_label_set_text(title_label, state.title[0] ? state.title : "Nothing Playing");
        lv_obj_clear_flag(title_label, LV_OBJ_FLAG_HIDDEN);
        lv_obj_add_flag(title_canvas, LV_OBJ_FLAG_HIDDEN);
    }

    if (state.artist_bitmap_valid) {
        draw_1bit(artist_canvas, artist_canvas_buf,
                  state.artist_bitmap, sizeof(state.artist_bitmap),
                  LUMI_TEXT_W, LUMI_ARTIST_H, 0xA8A8AD);
        lv_obj_add_flag(artist_label, LV_OBJ_FLAG_HIDDEN);
        lv_obj_clear_flag(artist_canvas, LV_OBJ_FLAG_HIDDEN);
    } else {
        lv_label_set_text(artist_label, state.artist[0] ? state.artist : "Lumi MacroPad");
        lv_obj_clear_flag(artist_label, LV_OBJ_FLAG_HIDDEN);
        lv_obj_add_flag(artist_canvas, LV_OBJ_FLAG_HIDDEN);
    }

    uint32_t max = state.duration_ms ? state.duration_ms : 1U;
    uint32_t pos = state.position_ms > max ? max : state.position_ms;
    bool playing = state.playing;

    k_mutex_unlock(&state_lock);

    lv_bar_set_range(progress, 0, 1000);
    lv_bar_set_value(progress, (int32_t)((pos * 1000ULL) / max), LV_ANIM_OFF);

    char a[12], b[12];
    fmt_time(a, sizeof(a), pos);
    fmt_time(b, sizeof(b), max);
    lv_label_set_text(elapsed_label, a);
    lv_label_set_text(remain_label, b);
    lv_label_set_text(play_label, playing ? "PLAYING" : "PAUSED");

    lv_obj_clear_flag(page, LV_OBJ_FLAG_HIDDEN);
    lv_obj_move_foreground(page);
}

void lumi_now_playing_update(const char *title, const char *artist,
                             uint32_t position_ms, uint32_t duration_ms,
                             bool playing) {
    k_mutex_lock(&state_lock, K_FOREVER);

    bool title_changed = strncmp(state.title, title ? title : "", sizeof(state.title)) != 0;
    bool artist_changed = strncmp(state.artist, artist ? artist : "", sizeof(state.artist)) != 0;

    snprintf(state.title, sizeof(state.title), "%s", title ? title : "");
    snprintf(state.artist, sizeof(state.artist), "%s", artist ? artist : "");
    state.position_ms = position_ms;
    state.duration_ms = duration_ms;
    state.playing = playing;
    state.last_rx_ms = k_uptime_get_32();

    if (title_changed) {
        state.title_bitmap_valid = false;
    }
    if (artist_changed) {
        state.artist_bitmap_valid = false;
    }

    k_mutex_unlock(&state_lock);

    if (ui_ready) {
        k_work_submit_to_queue(zmk_display_work_q(), &music_work);
    }
}

void lumi_now_playing_set_bitmap(bool title_bitmap,
                                 const uint8_t *data, size_t len) {
    if (!data) {
        return;
    }

    k_mutex_lock(&state_lock, K_FOREVER);

    if (title_bitmap) {
        size_t copy = MIN(len, sizeof(state.title_bitmap));
        memset(state.title_bitmap, 0, sizeof(state.title_bitmap));
        memcpy(state.title_bitmap, data, copy);
        state.title_bitmap_valid = true;
    } else {
        size_t copy = MIN(len, sizeof(state.artist_bitmap));
        memset(state.artist_bitmap, 0, sizeof(state.artist_bitmap));
        memcpy(state.artist_bitmap, data, copy);
        state.artist_bitmap_valid = true;
    }

    state.last_rx_ms = k_uptime_get_32();
    k_mutex_unlock(&state_lock);

    if (ui_ready) {
        k_work_submit_to_queue(zmk_display_work_q(), &music_work);
    }
}

static void music_visibility_timer(lv_timer_t *timer) {
    ARG_UNUSED(timer);

    uint32_t last;
    k_mutex_lock(&state_lock, K_FOREVER);
    last = state.last_rx_ms;
    k_mutex_unlock(&state_lock);

    if (last != 0U && (uint32_t)(k_uptime_get_32() - last) > MUSIC_TIMEOUT_MS) {
        lv_obj_add_flag(page, LV_OBJ_FLAG_HIDDEN);
    }
}

static lv_obj_t *make_text(lv_obj_t *parent, const lv_font_t *font, uint32_t color) {
    lv_obj_t *label = lv_label_create(parent);
    lv_obj_set_style_text_font(label, font, 0);
    lv_obj_set_style_text_color(label, lv_color_hex(color), 0);
    return label;
}

void lumi_now_playing_init(lv_obj_t *screen) {
    page = lv_obj_create(screen);
    lv_obj_remove_style_all(page);
    lv_obj_set_pos(page, 0, 0);
    lv_obj_set_size(page, 320, 172);
    lv_obj_set_style_bg_color(page, lv_color_hex(0x000000), 0);
    lv_obj_set_style_bg_opa(page, LV_OPA_COVER, 0);
    lv_obj_clear_flag(page, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_add_flag(page, LV_OBJ_FLAG_HIDDEN);

    lv_obj_t *header = make_text(page, &lv_font_montserrat_12, 0xA8A8AD);
    lv_label_set_text(header, "NOW PLAYING");
    lv_obj_set_pos(header, 12, 8);

    album_card = lv_obj_create(page);
    lv_obj_remove_style_all(album_card);
    lv_obj_set_pos(album_card, 12, 32);
    lv_obj_set_size(album_card, 76, 76);
    lv_obj_set_style_bg_color(album_card, lv_color_hex(0x171719), 0);
    lv_obj_set_style_bg_opa(album_card, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(album_card, 14, 0);
    lv_obj_set_style_border_width(album_card, 1, 0);
    lv_obj_set_style_border_color(album_card, lv_color_hex(0x3A3A3C), 0);

    album_icon = make_text(album_card, &lv_font_montserrat_20, 0xFFFFFF);
    lv_label_set_text(album_icon, LV_SYMBOL_PLAY);
    lv_obj_center(album_icon);

    title_label = make_text(page, &lv_font_montserrat_16, 0xFFFFFF);
    lv_obj_set_pos(title_label, 102, 34);
    lv_obj_set_width(title_label, LUMI_TEXT_W);
    lv_label_set_long_mode(title_label, LV_LABEL_LONG_DOT);
    lv_label_set_text(title_label, "Nothing Playing");

    title_canvas = lv_canvas_create(page);
    lv_canvas_set_buffer(title_canvas, title_canvas_buf,
                         LUMI_TEXT_W, LUMI_TITLE_H, LV_IMG_CF_TRUE_COLOR);
    lv_obj_set_pos(title_canvas, 102, 32);
    lv_obj_add_flag(title_canvas, LV_OBJ_FLAG_HIDDEN);

    artist_label = make_text(page, &lv_font_montserrat_14, 0xA8A8AD);
    lv_obj_set_pos(artist_label, 102, 58);
    lv_obj_set_width(artist_label, LUMI_TEXT_W);
    lv_label_set_long_mode(artist_label, LV_LABEL_LONG_DOT);
    lv_label_set_text(artist_label, "Lumi MacroPad");

    artist_canvas = lv_canvas_create(page);
    lv_canvas_set_buffer(artist_canvas, artist_canvas_buf,
                         LUMI_TEXT_W, LUMI_ARTIST_H, LV_IMG_CF_TRUE_COLOR);
    lv_obj_set_pos(artist_canvas, 102, 57);
    lv_obj_add_flag(artist_canvas, LV_OBJ_FLAG_HIDDEN);

    progress = lv_bar_create(page);
    lv_obj_set_pos(progress, 102, 87);
    lv_obj_set_size(progress, 206, 4);
    lv_obj_set_style_radius(progress, 2, LV_PART_MAIN);
    lv_obj_set_style_radius(progress, 2, LV_PART_INDICATOR);
    lv_obj_set_style_bg_color(progress, lv_color_hex(0x2C2C2E), LV_PART_MAIN);
    lv_obj_set_style_bg_opa(progress, LV_OPA_COVER, LV_PART_MAIN);
    lv_obj_set_style_bg_color(progress, lv_color_hex(0xFFFFFF), LV_PART_INDICATOR);
    lv_obj_set_style_bg_opa(progress, LV_OPA_COVER, LV_PART_INDICATOR);

    elapsed_label = make_text(page, &lv_font_montserrat_12, 0x8E8E93);
    lv_obj_set_pos(elapsed_label, 102, 96);
    lv_label_set_text(elapsed_label, "0:00");

    remain_label = make_text(page, &lv_font_montserrat_12, 0x8E8E93);
    lv_obj_set_pos(remain_label, 272, 96);
    lv_obj_set_width(remain_label, 36);
    lv_obj_set_style_text_align(remain_label, LV_TEXT_ALIGN_RIGHT, 0);
    lv_label_set_text(remain_label, "0:00");

    lv_obj_t *prev = make_text(page, &lv_font_montserrat_20, 0xFFFFFF);
    lv_label_set_text(prev, LV_SYMBOL_PREV);
    lv_obj_set_pos(prev, 112, 127);

    lv_obj_t *play = make_text(page, &lv_font_montserrat_20, 0xFFFFFF);
    lv_label_set_text(play, LV_SYMBOL_PLAY);
    lv_obj_set_pos(play, 190, 127);

    lv_obj_t *next = make_text(page, &lv_font_montserrat_20, 0xFFFFFF);
    lv_label_set_text(next, LV_SYMBOL_NEXT);
    lv_obj_set_pos(next, 264, 127);

    play_label = make_text(page, &lv_font_montserrat_12, 0x8E8E93);
    lv_label_set_text(play_label, "PAUSED");
    lv_obj_set_pos(play_label, 12, 126);

    ui_ready = true;
    lv_timer_create(music_visibility_timer, 500, NULL);
}
