/* SPDX-License-Identifier: MIT */

#include <stdio.h>
#include <string.h>

#include <lvgl.h>
#include <zephyr/kernel.h>
#include <zmk/battery.h>
#include <zmk/display.h>
#include <zmk/keymap.h>

#include "lumi_now_playing.h"
#include "lumi_ui_config.h"

#define MUSIC_TIMEOUT_MS 3500
#define USER_ACTIVITY_HIDE_MS 10000
#define SCROLL_STEP_MS 75
#define SCROLL_HOLD_MS 650

struct music_state {
    char source[20];
    char title[64];
    char artist[48];
    uint32_t position_ms;
    uint32_t duration_ms;
    bool playing;
    bool active;
    uint32_t last_rx_ms;
    uint32_t suppress_until_ms;

    uint8_t title_bitmap[LUMI_TITLE_BITMAP_MAX_BYTES];
    uint8_t artist_bitmap[LUMI_ARTIST_BITMAP_MAX_BYTES];
    size_t title_bitmap_len;
    size_t artist_bitmap_len;
    uint16_t title_bitmap_width;
    uint16_t artist_bitmap_width;
    bool title_bitmap_valid;
    bool artist_bitmap_valid;

    uint8_t artwork[LUMI_ARTWORK_BYTES];
    bool artwork_valid;

    uint16_t title_scroll;
    uint16_t artist_scroll;
    int8_t title_scroll_dir;
    int8_t artist_scroll_dir;
    uint32_t title_hold_until;
    uint32_t artist_hold_until;
    uint32_t last_scroll_ms;
};

static struct music_state state = {
    .title_scroll_dir = 1,
    .artist_scroll_dir = 1,
};
K_MUTEX_DEFINE(state_lock);

static lv_obj_t *page;
static lv_obj_t *source_label;
static lv_obj_t *battery_label;
static lv_obj_t *album_card;
static lv_obj_t *album_icon;
static lv_obj_t *album_canvas;
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
static lv_color_t album_canvas_buf[LUMI_ARTWORK_BYTES];

static bool ui_ready;
static bool page_visible;
static uint32_t last_header_ms;

static bool time_before(uint32_t now, uint32_t target) {
    return (int32_t)(now - target) < 0;
}

static void fmt_time(char *out, size_t len, uint32_t ms) {
    uint32_t s = ms / 1000U;
    snprintf(out, len, "%u:%02u", (unsigned)(s / 60U), (unsigned)(s % 60U));
}

static void draw_1bit_window(lv_obj_t *canvas, lv_color_t *dst,
                             const uint8_t *bits, size_t bit_len,
                             uint16_t source_width, uint16_t source_height,
                             uint16_t offset_x, uint32_t fg_hex) {
    lv_color_t fg = lv_color_hex(fg_hex);
    lv_color_t bg = lv_color_hex(0x000000);

    for (uint16_t y = 0; y < source_height; y++) {
        for (uint16_t x = 0; x < LUMI_TEXT_W; x++) {
            uint16_t sx = x + offset_x;
            bool on = false;

            if (sx < source_width) {
                size_t src_index = (size_t)y * source_width + sx;
                size_t byte_i = src_index >> 3;
                uint8_t mask = (uint8_t)(0x80U >> (src_index & 7U));
                on = byte_i < bit_len && (bits[byte_i] & mask);
            }

            dst[(size_t)y * LUMI_TEXT_W + x] = on ? fg : bg;
        }
    }

    lv_obj_invalidate(canvas);
}

static void draw_artwork_locked(void) {
    if (!state.artwork_valid) {
        lv_obj_clear_flag(album_card, LV_OBJ_FLAG_HIDDEN);
        lv_obj_add_flag(album_canvas, LV_OBJ_FLAG_HIDDEN);
        return;
    }

    for (size_t i = 0; i < LUMI_ARTWORK_BYTES; i++) {
        uint8_t v = state.artwork[i];
        uint8_t r = (uint8_t)((((v >> 5) & 0x07U) * 255U) / 7U);
        uint8_t g = (uint8_t)((((v >> 2) & 0x07U) * 255U) / 7U);
        uint8_t b = (uint8_t)(((v & 0x03U) * 255U) / 3U);
        album_canvas_buf[i] = lv_color_make(r, g, b);
    }

    lv_obj_add_flag(album_card, LV_OBJ_FLAG_HIDDEN);
    lv_obj_clear_flag(album_canvas, LV_OBJ_FLAG_HIDDEN);
    lv_obj_invalidate(album_canvas);
}

static void page_anim_x_cb(void *obj, int32_t value) {
    lv_obj_set_x((lv_obj_t *)obj, value);
}

static void page_anim_opa_cb(void *obj, int32_t value) {
    lv_obj_set_style_opa((lv_obj_t *)obj, value, 0);
}

static void hide_music_page(void) {
    if (!page_visible) {
        return;
    }

    page_visible = false;
    lv_anim_del(page, page_anim_x_cb);
    lv_anim_del(page, page_anim_opa_cb);
    lv_obj_set_x(page, 0);
    lv_obj_set_style_opa(page, LV_OPA_COVER, 0);
    lv_obj_add_flag(page, LV_OBJ_FLAG_HIDDEN);
}

static void show_music_page_animated(void) {
    if (page_visible) {
        return;
    }

    page_visible = true;

    lv_obj_clear_flag(page, LV_OBJ_FLAG_HIDDEN);
    lv_obj_move_foreground(page);
    lv_obj_set_x(page, 18);
    lv_obj_set_style_opa(page, 0, 0);

    lv_anim_t a;

    lv_anim_init(&a);
    lv_anim_set_var(&a, page);
    lv_anim_set_exec_cb(&a, page_anim_x_cb);
    lv_anim_set_values(&a, 18, 0);
    lv_anim_set_time(&a, 280);
    lv_anim_set_path_cb(&a, lv_anim_path_ease_out);
    lv_anim_start(&a);

    lv_anim_init(&a);
    lv_anim_set_var(&a, page);
    lv_anim_set_exec_cb(&a, page_anim_opa_cb);
    lv_anim_set_values(&a, 0, 255);
    lv_anim_set_time(&a, 220);
    lv_anim_set_path_cb(&a, lv_anim_path_ease_out);
    lv_anim_start(&a);
}

static void update_header_status(void) {
    if (!source_label || !battery_label) {
        return;
    }

    char source[20];
    k_mutex_lock(&state_lock, K_FOREVER);
    snprintf(source, sizeof(source), "%s",
             state.source[0] ? state.source : "MUSIC");
    k_mutex_unlock(&state_lock);

    char battery[12];
    snprintf(battery, sizeof(battery), "%u%%",
             (unsigned int)zmk_battery_state_of_charge());

    lv_label_set_text(source_label, source);
    lv_label_set_text(battery_label, battery);
}

static bool should_show_locked(uint32_t now) {
    if (!state.active || state.last_rx_ms == 0U) {
        return false;
    }

    if ((uint32_t)(now - state.last_rx_ms) > MUSIC_TIMEOUT_MS) {
        return false;
    }

    if (state.suppress_until_ms != 0U &&
        time_before(now, state.suppress_until_ms)) {
        return false;
    }

    return true;
}

static void draw_text_bitmaps_locked(void) {
    if (state.title_bitmap_valid) {
        draw_1bit_window(title_canvas, title_canvas_buf,
                         state.title_bitmap, state.title_bitmap_len,
                         state.title_bitmap_width, LUMI_TITLE_H,
                         state.title_scroll, 0xFFFFFF);
        lv_obj_add_flag(title_label, LV_OBJ_FLAG_HIDDEN);
        lv_obj_clear_flag(title_canvas, LV_OBJ_FLAG_HIDDEN);
    } else {
        lv_label_set_text(title_label,
                          state.title[0] ? state.title : "Now Playing");
        lv_obj_clear_flag(title_label, LV_OBJ_FLAG_HIDDEN);
        lv_obj_add_flag(title_canvas, LV_OBJ_FLAG_HIDDEN);
    }

    if (state.artist_bitmap_valid) {
        draw_1bit_window(artist_canvas, artist_canvas_buf,
                         state.artist_bitmap, state.artist_bitmap_len,
                         state.artist_bitmap_width, LUMI_ARTIST_H,
                         state.artist_scroll, 0xA8A8AD);
        lv_obj_add_flag(artist_label, LV_OBJ_FLAG_HIDDEN);
        lv_obj_clear_flag(artist_canvas, LV_OBJ_FLAG_HIDDEN);
    } else {
        lv_label_set_text(artist_label,
                          state.artist[0] ? state.artist : "LumiPad");
        lv_obj_clear_flag(artist_label, LV_OBJ_FLAG_HIDDEN);
        lv_obj_add_flag(artist_canvas, LV_OBJ_FLAG_HIDDEN);
    }
}

static void apply_music_state(struct k_work *work);
K_WORK_DEFINE(music_work, apply_music_state);

static void apply_music_state(struct k_work *work) {
    ARG_UNUSED(work);

    if (!ui_ready) {
        return;
    }

    uint32_t now = k_uptime_get_32();

    k_mutex_lock(&state_lock, K_FOREVER);

    if (!should_show_locked(now)) {
        k_mutex_unlock(&state_lock);
        hide_music_page();
        return;
    }

    draw_text_bitmaps_locked();
    draw_artwork_locked();

    uint32_t max = state.duration_ms ? state.duration_ms : 1U;
    uint32_t pos = state.position_ms > max ? max : state.position_ms;

    k_mutex_unlock(&state_lock);

    lv_bar_set_range(progress, 0, 1000);
    lv_bar_set_value(progress, (int32_t)((pos * 1000ULL) / max), LV_ANIM_OFF);

    char a[12], b[12];
    fmt_time(a, sizeof(a), pos);
    fmt_time(b, sizeof(b), max);
    lv_label_set_text(elapsed_label, a);
    lv_label_set_text(remain_label, b);
    bool playing_now;
    k_mutex_lock(&state_lock, K_FOREVER);
    playing_now = state.playing;
    k_mutex_unlock(&state_lock);
    lv_label_set_text(play_label, playing_now ? "PLAYING" : "PAUSED");

    update_header_status();
    show_music_page_animated();
}

void lumi_now_playing_update(const char *source,
                             const char *title, const char *artist,
                             uint32_t position_ms, uint32_t duration_ms,
                             bool playing) {
    k_mutex_lock(&state_lock, K_FOREVER);

    snprintf(state.source, sizeof(state.source), "%s",
             source && source[0] ? source : "MUSIC");
    snprintf(state.title, sizeof(state.title), "%s", title ? title : "");
    snprintf(state.artist, sizeof(state.artist), "%s", artist ? artist : "");
    state.position_ms = position_ms;
    state.duration_ms = duration_ms;
    state.playing = playing;
    state.active = true;
    state.last_rx_ms = k_uptime_get_32();

    k_mutex_unlock(&state_lock);

    lumi_ui_set_media_active(true);

    if (ui_ready) {
        k_work_submit_to_queue(zmk_display_work_q(), &music_work);
    }
}

bool lumi_now_playing_is_active(void) {
    bool active;
    uint32_t now = k_uptime_get_32();

    k_mutex_lock(&state_lock, K_FOREVER);
    active = state.active &&
             state.last_rx_ms != 0U &&
             (uint32_t)(now - state.last_rx_ms) <= MUSIC_TIMEOUT_MS;
    k_mutex_unlock(&state_lock);

    return active;
}

void lumi_now_playing_set_bitmap(bool title_bitmap, uint16_t width,
                                 const uint8_t *data, size_t len) {
    if (!data || width < LUMI_TEXT_W) {
        return;
    }

    k_mutex_lock(&state_lock, K_FOREVER);

    if (title_bitmap) {
        if (width > LUMI_TITLE_BITMAP_MAX_W ||
            len > sizeof(state.title_bitmap)) {
            k_mutex_unlock(&state_lock);
            return;
        }

        memset(state.title_bitmap, 0, sizeof(state.title_bitmap));
        memcpy(state.title_bitmap, data, len);
        state.title_bitmap_len = len;
        state.title_bitmap_width = width;
        state.title_bitmap_valid = true;
        state.title_scroll = 0;
        state.title_scroll_dir = 1;
        state.title_hold_until = k_uptime_get_32() + SCROLL_HOLD_MS;
    } else {
        if (width > LUMI_ARTIST_BITMAP_MAX_W ||
            len > sizeof(state.artist_bitmap)) {
            k_mutex_unlock(&state_lock);
            return;
        }

        memset(state.artist_bitmap, 0, sizeof(state.artist_bitmap));
        memcpy(state.artist_bitmap, data, len);
        state.artist_bitmap_len = len;
        state.artist_bitmap_width = width;
        state.artist_bitmap_valid = true;
        state.artist_scroll = 0;
        state.artist_scroll_dir = 1;
        state.artist_hold_until = k_uptime_get_32() + SCROLL_HOLD_MS;
    }

    if (state.active) {
        state.last_rx_ms = k_uptime_get_32();
    }

    k_mutex_unlock(&state_lock);

    if (ui_ready) {
        k_work_submit_to_queue(zmk_display_work_q(), &music_work);
    }
}

void lumi_now_playing_set_artwork(const uint8_t *data, size_t len) {
    if (!data || len != LUMI_ARTWORK_BYTES) {
        return;
    }

    k_mutex_lock(&state_lock, K_FOREVER);
    memcpy(state.artwork, data, LUMI_ARTWORK_BYTES);
    state.artwork_valid = true;

    if (state.playing) {
        state.last_rx_ms = k_uptime_get_32();
    }

    k_mutex_unlock(&state_lock);

    if (ui_ready) {
        k_work_submit_to_queue(zmk_display_work_q(), &music_work);
    }
}

void lumi_now_playing_user_activity(void) {
    k_mutex_lock(&state_lock, K_FOREVER);
    state.suppress_until_ms = k_uptime_get_32() + USER_ACTIVITY_HIDE_MS;
    k_mutex_unlock(&state_lock);

    if (ui_ready) {
        k_work_submit_to_queue(zmk_display_work_q(), &music_work);
    }
}

void lumi_now_playing_clear(void) {
    k_mutex_lock(&state_lock, K_FOREVER);
    state.playing = false;
    state.active = false;
    state.last_rx_ms = 0U;
    state.title_bitmap_valid = false;
    state.artist_bitmap_valid = false;
    state.artwork_valid = false;
    state.title_scroll = 0;
    state.artist_scroll = 0;
    k_mutex_unlock(&state_lock);

    lumi_ui_set_media_active(false);

    if (ui_ready) {
        k_work_submit_to_queue(zmk_display_work_q(), &music_work);
    }
}

static void advance_scroll(uint32_t now, uint16_t width,
                           uint16_t *offset, int8_t *dir,
                           uint32_t *hold_until) {
    if (width <= LUMI_TEXT_W) {
        *offset = 0;
        *dir = 1;
        return;
    }

    if (*hold_until != 0U && time_before(now, *hold_until)) {
        return;
    }

    uint16_t max_offset = width - LUMI_TEXT_W;

    if (*dir > 0) {
        if (*offset < max_offset) {
            (*offset)++;
        }
        if (*offset >= max_offset) {
            *offset = max_offset;
            *dir = -1;
            *hold_until = now + SCROLL_HOLD_MS;
        }
    } else {
        if (*offset > 0) {
            (*offset)--;
        }
        if (*offset == 0) {
            *dir = 1;
            *hold_until = now + SCROLL_HOLD_MS;
        }
    }
}

static void music_visibility_timer(lv_timer_t *timer) {
    ARG_UNUSED(timer);

    if (!ui_ready) {
        return;
    }

    uint32_t now = k_uptime_get_32();
    bool show;
    bool redraw = false;

    k_mutex_lock(&state_lock, K_FOREVER);
    show = should_show_locked(now);

    if (show && (uint32_t)(now - state.last_scroll_ms) >= SCROLL_STEP_MS) {
        uint16_t old_title = state.title_scroll;
        uint16_t old_artist = state.artist_scroll;

        if (state.title_bitmap_valid) {
            advance_scroll(now, state.title_bitmap_width,
                           &state.title_scroll, &state.title_scroll_dir,
                           &state.title_hold_until);
        }
        if (state.artist_bitmap_valid) {
            advance_scroll(now, state.artist_bitmap_width,
                           &state.artist_scroll, &state.artist_scroll_dir,
                           &state.artist_hold_until);
        }

        state.last_scroll_ms = now;
        redraw = old_title != state.title_scroll ||
                 old_artist != state.artist_scroll;

        if (redraw) {
            draw_text_bitmaps_locked();
        }
    }

    k_mutex_unlock(&state_lock);

    if ((uint32_t)(now - last_header_ms) >= 500U) {
        last_header_ms = now;
        update_header_status();
    }

    if (!show) {
        hide_music_page();
    } else {
        show_music_page_animated();
    }
}

static lv_obj_t *make_text(lv_obj_t *parent, const lv_font_t *font,
                           uint32_t color) {
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
    lv_obj_set_width(header, 92);

    source_label = make_text(page, &lv_font_montserrat_12, 0xFFFFFF);
    lv_obj_set_pos(source_label, 104, 8);
    lv_obj_set_width(source_label, 112);
    lv_obj_set_style_text_align(source_label, LV_TEXT_ALIGN_CENTER, 0);
    lv_label_set_long_mode(source_label, LV_LABEL_LONG_DOT);
    lv_label_set_text(source_label, "MUSIC");

    battery_label = make_text(page, &lv_font_montserrat_12, 0xA8A8AD);
    lv_obj_set_pos(battery_label, 238, 8);
    lv_obj_set_width(battery_label, 70);
    lv_obj_set_style_text_align(battery_label, LV_TEXT_ALIGN_RIGHT, 0);
    lv_label_set_text(battery_label, "100%");

    album_card = lv_obj_create(page);
    lv_obj_remove_style_all(album_card);
    lv_obj_set_pos(album_card, 12, 32);
    lv_obj_set_size(album_card, 76, 76);
    lv_obj_set_style_bg_color(album_card, lv_color_hex(0x171719), 0);
    lv_obj_set_style_bg_opa(album_card, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(album_card, 14, 0);
    lv_obj_set_style_border_width(album_card, 1, 0);
    lv_obj_set_style_border_color(album_card, lv_color_hex(0x3A3A3C), 0);

    album_icon = make_text(album_card, &lv_font_montserrat_20, 0xFF9F0A);
    lv_label_set_text(album_icon, LV_SYMBOL_PLAY);
    lv_obj_center(album_icon);

    album_canvas = lv_canvas_create(page);
    lv_canvas_set_buffer(album_canvas, album_canvas_buf,
                         LUMI_ARTWORK_W, LUMI_ARTWORK_H,
                         LV_IMG_CF_TRUE_COLOR);
    lv_obj_set_pos(album_canvas, 12, 32);
    lv_obj_add_flag(album_canvas, LV_OBJ_FLAG_HIDDEN);

    title_label = make_text(page, &lv_font_montserrat_16, 0xFFFFFF);
    lv_obj_set_pos(title_label, 102, 34);
    lv_obj_set_width(title_label, LUMI_TEXT_W);
    lv_label_set_long_mode(title_label, LV_LABEL_LONG_DOT);
    lv_label_set_text(title_label, "Now Playing");

    title_canvas = lv_canvas_create(page);
    lv_canvas_set_buffer(title_canvas, title_canvas_buf,
                         LUMI_TEXT_W, LUMI_TITLE_H, LV_IMG_CF_TRUE_COLOR);
    lv_obj_set_pos(title_canvas, 102, 32);
    lv_obj_add_flag(title_canvas, LV_OBJ_FLAG_HIDDEN);

    artist_label = make_text(page, &lv_font_montserrat_14, 0xA8A8AD);
    lv_obj_set_pos(artist_label, 102, 58);
    lv_obj_set_width(artist_label, LUMI_TEXT_W);
    lv_label_set_long_mode(artist_label, LV_LABEL_LONG_DOT);
    lv_label_set_text(artist_label, "LumiPad");

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

    lv_obj_t *prev = make_text(page, &lv_font_montserrat_20, 0x64D2FF);
    lv_label_set_text(prev, LV_SYMBOL_PREV);
    lv_obj_set_pos(prev, 112, 127);

    lv_obj_t *play = make_text(page, &lv_font_montserrat_20, 0x30D158);
    lv_label_set_text(play, LV_SYMBOL_PLAY);
    lv_obj_set_pos(play, 190, 127);

    lv_obj_t *next = make_text(page, &lv_font_montserrat_20, 0x64D2FF);
    lv_label_set_text(next, LV_SYMBOL_NEXT);
    lv_obj_set_pos(next, 264, 127);

    play_label = make_text(page, &lv_font_montserrat_12, 0x8E8E93);
    lv_label_set_text(play_label, "PLAYING");
    lv_obj_set_pos(play_label, 12, 126);

    ui_ready = true;
    page_visible = false;
    update_header_status();
    lv_timer_create(music_visibility_timer, 75, NULL);
}
