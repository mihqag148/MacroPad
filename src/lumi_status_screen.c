/* SPDX-License-Identifier: MIT
 * Lumi MacroPad: four columns, three rows, live ZMK keymap captions.
 */
#include <stdio.h>
#include <string.h>
#include <zephyr/device.h>
#include <zephyr/devicetree.h>
#include <zephyr/drivers/display.h>
#include <zephyr/kernel.h>
#include <zephyr/storage/flash_map.h>
#include <zephyr/sys/atomic.h>
#include <zephyr/sys/poweroff.h>
#include <lvgl.h>
#include <dt-bindings/zmk/keys.h>
#include <zmk/activity.h>
#include <zmk/behavior.h>
#include <zmk/ble.h>
#include <zmk/display.h>
#include <zmk/display/status_screen.h>
#include <zmk/display/widgets/battery_status.h>
#include <zmk/endpoints.h>
#include <zmk/event_manager.h>
#include <zmk/events/ble_active_profile_changed.h>
#include <zmk/events/endpoint_changed.h>
#include <zmk/events/layer_state_changed.h>
#include <zmk/events/position_state_changed.h>
#include <zmk/events/keycode_state_changed.h>
#include <zmk/keys.h>
#include <zmk/keymap.h>
#include <zmk/pm.h>
#include <zmk/usb.h>
#include "lumi_panel.h"
#include "lumi_now_playing.h"
#include "lumi_rgb.h"
#include "lumi_ui_config.h"
#include "lumi_diag.h"

#define KEY_COUNT 12
#define COLS 4
#define CELL_W 80
#define CELL_H 49
#define STATUS_H 25
#define PRESS_MIN_MS 100

static lv_obj_t *tiles[KEY_COUNT], *icons[KEY_COUNT], *captions[KEY_COUNT];
static lv_obj_t *layer_label, *output_label;
static struct zmk_widget_battery_status battery_widget;
static atomic_t held_keys, tapped_keys;
static atomic_t popup_action;

static lv_obj_t *popup;
static lv_obj_t *popup_icon;
static lv_obj_t *popup_text;

static lv_obj_t *screensaver;
static lv_obj_t *saver_orb1;
static lv_obj_t *saver_orb2;
static lv_obj_t *saver_glass;
static lv_obj_t *saver_title;
#define SAVER_STRIPE_SRC_ROWS 4U
#define SAVER_STRIPE_DST_ROWS (SAVER_STRIPE_SRC_ROWS * 2U)

static const struct device *const saver_display =
    DEVICE_DT_GET(DT_CHOSEN(zephyr_display));
static lv_color_t saver_stripe_buf[320U * SAVER_STRIPE_DST_ROWS];
static lv_color_t saver_rgb332_lut[256];
static bool saver_rgb332_lut_ready;

#define SAVER_FLASH_MAGIC 0x4C534156U /* "LSAV" */
#define SAVER_FLASH_VERSION 1U
#define SAVER_FLASH_DATA_OFFSET 0x1000U
#define SAVER_FLASH_PAGE_SIZE 0x1000U
#define SAVER_FLASH_MAX_PAGES 86U
#define SAVER_FLASH_TIMING_OFFSET 0x40U
#define SAVER_FLASH_TIMING_MAGIC 0x4D495453U /* "STIM" */

struct saver_flash_header {
    uint32_t magic;
    uint16_t version;
    uint16_t width;
    uint16_t height;
    uint16_t frame_bytes;
    uint8_t frame_count;
    uint8_t reserved;
    uint16_t interval_ms;
    uint32_t data_size;
};

struct saver_flash_timing {
    uint32_t magic;
    uint8_t frame_count;
    uint8_t reserved0;
    uint16_t reserved1;
    uint16_t interval_ms[LUMI_SAVER_MAX_FRAMES];
    uint16_t reserved2;
};

static const struct flash_area *saver_flash;
static bool saver_flash_checked;
static uint32_t saver_flash_erased_pages[3];
static uint8_t saver_media_frame_buffer[LUMI_SAVER_FRAME_BYTES];
static uint8_t saver_prefetched_index;
static bool saver_prefetch_valid;
static uint8_t saver_media_frame_count;
static uint32_t saver_media_received_mask;
static uint16_t saver_media_received_bytes[LUMI_SAVER_MAX_FRAMES];
static uint16_t saver_media_interval_ms = 40;
static uint16_t saver_media_frame_intervals[LUMI_SAVER_MAX_FRAMES];
static uint32_t saver_media_loop_ms = 40U;
static uint8_t saver_media_index;
static uint32_t saver_media_epoch_ms;
static bool saver_media_valid;
static bool screensaver_visible;
static lv_obj_t *root_screen;
static lv_obj_t *sleep_overlay;

K_MUTEX_DEFINE(lumi_ui_config_lock);
static uint32_t ui_last_activity_ms;
static uint32_t saver_delay_ms = 60000U;
static uint32_t sleep_delay_ms = 120000U;
static bool saver_enabled = true;
static uint8_t saver_style = LUMI_SAVER_OFF;
static uint32_t wallpaper_color_a = 0x000000;
static uint32_t wallpaper_color_b = 0x10151F;
static uint32_t saver_color_a = 0x4A7DFF;
static uint32_t saver_color_b = 0xA955FF;
static bool wallpaper_dirty = true;
static bool saver_style_dirty = true;
static bool media_active = false;
static bool soft_sleep = false;
static bool saver_force_show = false;

static uint32_t popup_until;
static bool popup_visible = false;

enum {
    POPUP_NONE = 0,
    POPUP_VOL_UP,
    POPUP_VOL_DOWN,
    POPUP_NEXT,
    POPUP_PREVIOUS,
    POPUP_PAGE_UP,
    POPUP_PAGE_DOWN,
};
static uint32_t press_started[KEY_COUNT];
static bool highlighted[KEY_COUNT];
static uint32_t icon_colors[KEY_COUNT];
static lv_color_t accent;

/* Slots are ZMK positions 0..11 in order. Position 12 is the encoder.
 * The wired 4x4 matrix and its transform remain unchanged.
 */
struct key_caption {
    char text[16];
    const char *icon;
    uint32_t color;
};
struct page_state {
    zmk_keymap_layer_id_t id;
    char name[32];
    struct key_caption keys[KEY_COUNT];
};

static void describe_key(uint32_t code, struct key_caption *out) {
    const char *text = NULL;
    out->icon = LV_SYMBOL_KEYBOARD;
    out->color = 0xFFFFFF;

    switch (code) {
    case LC(C): text = "COPY"; out->icon = LV_SYMBOL_COPY; out->color = 0x64D2FF; break;
    case LC(V): text = "PASTE"; out->icon = LV_SYMBOL_PASTE; out->color = 0x30D158; break;
    case LC(X): text = "CUT"; out->icon = LV_SYMBOL_CUT; out->color = 0xFF453A; break;
    case LC(Z): text = "UNDO"; out->icon = LV_SYMBOL_LEFT; out->color = 0x0A84FF; break;
    case LC(Y): text = "REDO"; out->icon = LV_SYMBOL_RIGHT; out->color = 0xBF5AF2; break;
    case LC(A): text = "SELECT ALL"; out->icon = LV_SYMBOL_LIST; out->color = 0xFFD60A; break;
    case LC(S): text = "SAVE"; out->icon = LV_SYMBOL_SAVE; out->color = 0xFF9F0A; break;
    case LC(F): text = "FIND"; out->icon = LV_SYMBOL_EYE_OPEN; out->color = 0x5AC8FA; break;
    case ENTER: text = "ENTER"; out->icon = LV_SYMBOL_OK; out->color = 0x30D158; break;
    case BACKSPACE: text = "BACKSPACE"; out->icon = LV_SYMBOL_BACKSPACE; out->color = 0xFF453A; break;
    case TAB: text = "TAB"; out->icon = LV_SYMBOL_RIGHT; out->color = 0x64D2FF; break;
    case ESC: text = "ESC"; out->icon = LV_SYMBOL_CLOSE; out->color = 0xFF9F0A; break;
    case DELETE: text = "DELETE"; out->icon = LV_SYMBOL_TRASH; out->color = 0xFF453A; break;
    case C_PLAY_PAUSE: text = "PLAY/PAUSE"; out->icon = LV_SYMBOL_PLAY; out->color = 0x30D158; break;
    case C_PREVIOUS: text = "PREVIOUS"; out->icon = LV_SYMBOL_PREV; out->color = 0x64D2FF; break;
    case C_NEXT: text = "NEXT"; out->icon = LV_SYMBOL_NEXT; out->color = 0x64D2FF; break;
    case C_STOP: text = "STOP"; out->icon = LV_SYMBOL_STOP; out->color = 0xFF453A; break;
    case C_MUTE: text = "MUTE"; out->icon = LV_SYMBOL_MUTE; out->color = 0xFF453A; break;
    case C_VOL_DN: text = "VOL -"; out->icon = LV_SYMBOL_VOLUME_MID; out->color = 0x0A84FF; break;
    case C_VOL_UP: text = "VOL +"; out->icon = LV_SYMBOL_VOLUME_MAX; out->color = 0x0A84FF; break;
    case C_AC_BACK: text = "BACK"; out->icon = LV_SYMBOL_LEFT; out->color = 0x5AC8FA; break;
    case C_AC_FORWARD: text = "FORWARD"; out->icon = LV_SYMBOL_RIGHT; out->color = 0x5AC8FA; break;
    case HOME: text = "HOME"; out->icon = LV_SYMBOL_HOME; out->color = 0xFF9F0A; break;
    case END: text = "END"; out->icon = LV_SYMBOL_DOWN; out->color = 0xBF5AF2; break;
    case PG_UP: text = "PAGE UP"; out->icon = LV_SYMBOL_UP; out->color = 0xBF5AF2; break;
    case PG_DN: text = "PAGE DOWN"; out->icon = LV_SYMBOL_DOWN; out->color = 0xBF5AF2; break;
    default: break;
    }

    if (text) {
        snprintf(out->text, sizeof(out->text), "%s", text);
    } else if (STRIP_MODS(code) >= A && STRIP_MODS(code) <= Z) {
        out->color = 0xD8F8FF;
        snprintf(out->text, sizeof(out->text), "%s%s%s%s%c",
                 SELECT_MODS(code) & (MOD_LCTL | MOD_RCTL) ? "C+" : "",
                 SELECT_MODS(code) & (MOD_LALT | MOD_RALT) ? "A+" : "",
                 SELECT_MODS(code) & (MOD_LSFT | MOD_RSFT) ? "S+" : "",
                 SELECT_MODS(code) & (MOD_LGUI | MOD_RGUI) ? "W+" : "",
                 (char)('A' + STRIP_MODS(code) - A));
    } else {
        out->color = 0xFFFFFF;
        snprintf(out->text, sizeof(out->text), "0x%08X", (unsigned int)code);
    }
}

static struct page_state read_page(const zmk_event_t *eh) {
    ARG_UNUSED(eh);
    struct page_state state = {0};
    zmk_keymap_layer_index_t index = zmk_keymap_highest_layer_active();
    state.id = zmk_keymap_layer_index_to_id(index);
    const char *name = zmk_keymap_layer_name(state.id);
    if (name && name[0]) {
        snprintf(state.name, sizeof(state.name), "%s", name);
    } else {
        snprintf(state.name, sizeof(state.name), "LAYER %u", index + 1);
    }
    for (uint8_t i = 0; i < KEY_COUNT; i++) {
        const struct zmk_behavior_binding *binding =
            zmk_keymap_get_layer_binding_at_idx(state.id, i);
        struct key_caption *key = &state.keys[i];
        key->icon = LV_SYMBOL_KEYBOARD;
        key->color = 0xFFFFFF;
        if (!binding || !binding->behavior_dev) {
            snprintf(key->text, sizeof(key->text), "--");
        } else if (strcmp(binding->behavior_dev, DEVICE_DT_NAME(DT_NODELABEL(kp))) == 0) {
            describe_key(binding->param1, key);
        } else {
            /* Never keep a stale COPY/etc label for a remapped behavior. */
            snprintf(key->text, sizeof(key->text), "%.8s %u",
                     binding->behavior_dev, (unsigned int)binding->param1);
        }
    }
    return state;
}

static void set_tile_pressed(uint8_t i, bool pressed) {
    lv_obj_set_style_bg_color(
        tiles[i],
        pressed ? lv_color_hex(0x18333D) : lv_color_hex(0x000000),
        0
    );

    lv_obj_set_style_bg_opa(
        tiles[i],
        pressed ? 205 : 78,
        0
    );

    lv_obj_set_style_border_color(
        tiles[i],
        lv_color_hex(0xFFFFFF),
        0
    );

    lv_obj_set_style_text_color(
        icons[i],
        lv_color_hex(0xFFFFFF),
        0
    );

    lv_obj_set_style_text_color(
        captions[i],
        lv_color_hex(0xFFFFFF),
        0
    );

    lv_obj_align(
        icons[i],
        LV_ALIGN_CENTER,
        0,
        pressed ? 3 : 0
    );
}

static void profile_anim_y_cb(void *obj, int32_t value) {
    lv_obj_set_y((lv_obj_t *)obj, value);
}

static void animate_profile_from_below(void) {
    lv_anim_t a;

    if (layer_label) {
        lv_anim_del(layer_label, profile_anim_y_cb);
        lv_obj_set_y(layer_label, 22);

        lv_anim_init(&a);
        lv_anim_set_var(&a, layer_label);
        lv_anim_set_exec_cb(&a, profile_anim_y_cb);
        lv_anim_set_values(&a, 22, 6);
        lv_anim_set_time(&a, 230);
        lv_anim_set_path_cb(&a, lv_anim_path_ease_out);
        lv_anim_start(&a);
    }

    for (uint8_t i = 0; i < KEY_COUNT; i++) {
        if (!tiles[i]) {
            continue;
        }

        uint8_t row = i / COLS;
        int32_t target_y = STATUS_H + row * CELL_H;

        lv_anim_del(tiles[i], profile_anim_y_cb);
        lv_obj_set_y(tiles[i], target_y + 18);

        lv_anim_init(&a);
        lv_anim_set_var(&a, tiles[i]);
        lv_anim_set_exec_cb(&a, profile_anim_y_cb);
        lv_anim_set_values(&a, target_y + 18, target_y);
        lv_anim_set_time(&a, 230);
        lv_anim_set_path_cb(&a, lv_anim_path_ease_out);
        lv_anim_start(&a);
    }
}

static void update_page(struct page_state state) {
    static struct page_state previous;
    static bool have_previous;
    bool same = have_previous && previous.id == state.id &&
                strcmp(previous.name, state.name) == 0;
    for (uint8_t i = 0; same && i < KEY_COUNT; i++) {
        same = previous.keys[i].icon == state.keys[i].icon &&
               previous.keys[i].color == state.keys[i].color &&
               strcmp(previous.keys[i].text, state.keys[i].text) == 0;
    }
    if (!layer_label || same) {
        return;
    }
    bool animate_change = have_previous && previous.id != state.id;
    previous = state;
    have_previous = true;
    const uint32_t colors[] = {0x5de0c3, 0xffc765, 0x7ebcff, 0xBF5AF2, 0xFF9F0A};
    accent = lv_color_hex(colors[state.id % ARRAY_SIZE(colors)]);
    lv_label_set_text(layer_label, state.name);
    lv_obj_set_style_text_color(layer_label, accent, 0);
    for (uint8_t i = 0; i < KEY_COUNT; i++) {
        lv_label_set_text(captions[i], state.keys[i].text);
        lv_label_set_text(icons[i], state.keys[i].icon);
        icon_colors[i] = state.keys[i].color;
        set_tile_pressed(i, highlighted[i]);
    }

    if (animate_change) {
        animate_profile_from_below();
    }
}

ZMK_DISPLAY_WIDGET_LISTENER(lumi_page, struct page_state, update_page, read_page)
ZMK_SUBSCRIPTION(lumi_page, zmk_layer_state_changed);

/* Studio edits do not all emit layer_state_changed in v0.3. Poll on the
 * system queue, copy state, and perform every LVGL call on the display queue.
 */
static void poll_page(struct k_work *work);
K_WORK_DELAYABLE_DEFINE(page_poll_work, poll_page);

static void poll_page(struct k_work *work) {
    ARG_UNUSED(work);
    lumi_page_refresh_state(NULL);
    k_work_submit_to_queue(zmk_display_work_q(), &lumi_page_work);
    k_work_schedule(&page_poll_work, K_MSEC(500));
}

struct output_state {
    struct zmk_endpoint_instance endpoint;
    bool connected;
    bool open;
};
static struct output_state read_output(const zmk_event_t *eh) {
    ARG_UNUSED(eh);
    return (struct output_state){
        .endpoint = zmk_endpoints_selected(),
        .connected = zmk_ble_active_profile_is_connected(),
        .open = zmk_ble_active_profile_is_open(),
    };
}
static void update_output(struct output_state state) {
    if (!output_label) {
        return;
    }
    if (state.endpoint.transport == ZMK_TRANSPORT_USB) {
        lv_label_set_text(output_label, LV_SYMBOL_USB " USB");
    } else {
        char text[24];
        snprintf(text, sizeof(text), "BLE%u %s", state.endpoint.ble.profile_index + 1,
                 state.connected ? LV_SYMBOL_OK :
                                   (state.open ? LV_SYMBOL_SETTINGS : LV_SYMBOL_CLOSE));
        lv_label_set_text(output_label, text);
    }
}
ZMK_DISPLAY_WIDGET_LISTENER(lumi_output, struct output_state, update_output, read_output)
ZMK_SUBSCRIPTION(lumi_output, zmk_endpoint_changed);
ZMK_SUBSCRIPTION(lumi_output, zmk_ble_active_profile_changed);

static int position_listener(const zmk_event_t *eh) {
    const struct zmk_position_state_changed *event = as_zmk_position_state_changed(eh);

    if (event && event->state) {
        lumi_ui_note_activity();

        /* Encoder push / non-keycode layer behavior has no keycode event.
         * Regular keys are classified below so media controls can stay on
         * the Now Playing screen.
         */
        if (event->position >= KEY_COUNT) {
            lumi_now_playing_user_activity();
        }
    }

    if (event && event->position < KEY_COUNT) {
        if (event->state) {
            atomic_set_bit(&held_keys, event->position);
            atomic_set_bit(&tapped_keys, event->position);
        } else {
            atomic_clear_bit(&held_keys, event->position);
        }
    }
    return ZMK_EV_EVENT_BUBBLE;
}
ZMK_LISTENER(lumi_keys, position_listener);
ZMK_SUBSCRIPTION(lumi_keys, zmk_position_state_changed);

static bool keycode_matches(
    const struct zmk_keycode_state_changed *event,
    uint32_t encoded
) {
    uint16_t page = ZMK_HID_USAGE_PAGE(encoded);

    if (page == 0) {
        page = HID_USAGE_KEY;
    }

    return event->usage_page == page &&
           event->keycode == ZMK_HID_USAGE_ID(encoded);
}

static int popup_keycode_listener(const zmk_event_t *eh) {
    const struct zmk_keycode_state_changed *event =
        as_zmk_keycode_state_changed(eh);

    if (!event || !event->state) {
        return ZMK_EV_EVENT_BUBBLE;
    }

    lumi_ui_note_activity();
    bool keep_music_visible = false;

    if (keycode_matches(event, C_VOL_UP)) {
        atomic_set(&popup_action, POPUP_VOL_UP);
        keep_music_visible = true;

    } else if (keycode_matches(event, C_VOL_DN)) {
        atomic_set(&popup_action, POPUP_VOL_DOWN);
        keep_music_visible = true;

    } else if (keycode_matches(event, C_NEXT)) {
        atomic_set(&popup_action, POPUP_NEXT);
        keep_music_visible = true;

    } else if (keycode_matches(event, C_PREVIOUS)) {
        atomic_set(&popup_action, POPUP_PREVIOUS);
        keep_music_visible = true;

    } else if (keycode_matches(event, C_PLAY_PAUSE) ||
               keycode_matches(event, C_MUTE)) {
        keep_music_visible = true;

    } else if (keycode_matches(event, PG_UP)) {
        atomic_set(&popup_action, POPUP_PAGE_UP);

    } else if (keycode_matches(event, PG_DN)) {
        atomic_set(&popup_action, POPUP_PAGE_DOWN);
    }

    if (!keep_music_visible) {
        lumi_now_playing_user_activity();
    }

    return ZMK_EV_EVENT_BUBBLE;
}

ZMK_LISTENER(lumi_popup, popup_keycode_listener);
ZMK_SUBSCRIPTION(lumi_popup, zmk_keycode_state_changed);
static void refresh_pressed(lv_timer_t *timer) {
    ARG_UNUSED(timer);
    uint32_t now = lv_tick_get();
    /* Atomic exchange retains taps that finish between display ticks. */
    atomic_val_t taps = atomic_set(&tapped_keys, 0);
    atomic_val_t held = atomic_get(&held_keys);
    for (uint8_t i = 0; i < KEY_COUNT; i++) {
        if (taps & BIT(i)) {
            press_started[i] = now;
        }
        bool pressed = (held & BIT(i)) || (taps & BIT(i)) ||
                       (highlighted[i] && (uint32_t)(now - press_started[i]) < PRESS_MIN_MS);
        if (pressed != highlighted[i]) {
            highlighted[i] = pressed;
            set_tile_pressed(i, pressed);
        }
    }
}

static lv_obj_t *make_label(lv_obj_t *parent, const lv_font_t *font) {
    lv_obj_t *label = lv_label_create(parent);
    lv_obj_set_style_text_font(label, font, 0);
    lv_obj_set_style_text_color(label, lv_color_white(), 0);
    return label;
}
static void popup_anim_y_cb(void *obj, int32_t value) {
    lv_obj_set_y((lv_obj_t *)obj, value);
}

static void popup_anim_opa_cb(void *obj, int32_t value) {
    lv_obj_set_style_opa((lv_obj_t *)obj, value, 0);
}

static void popup_animate(int32_t y_from,
                          int32_t y_to,
                          int32_t opa_from,
                          int32_t opa_to,
                          uint32_t time_ms,
                          bool showing) {

    lv_anim_del(popup, popup_anim_y_cb);
    lv_anim_del(popup, popup_anim_opa_cb);

    lv_anim_t a;

    lv_anim_init(&a);
    lv_anim_set_var(&a, popup);
    lv_anim_set_exec_cb(&a, popup_anim_y_cb);
    lv_anim_set_values(&a, y_from, y_to);
    lv_anim_set_time(&a, time_ms);
    lv_anim_set_path_cb(
        &a,
        showing ? lv_anim_path_ease_out : lv_anim_path_ease_in
    );
    lv_anim_start(&a);

    lv_anim_init(&a);
    lv_anim_set_var(&a, popup);
    lv_anim_set_exec_cb(&a, popup_anim_opa_cb);
    lv_anim_set_values(&a, opa_from, opa_to);
    lv_anim_set_time(&a, time_ms);
    lv_anim_set_path_cb(
        &a,
        showing ? lv_anim_path_ease_out : lv_anim_path_ease_in
    );
    lv_anim_start(&a);
}
static void refresh_popup(lv_timer_t *timer) {
    ARG_UNUSED(timer);

    int action = atomic_set(&popup_action, POPUP_NONE);
    uint32_t now = lv_tick_get();

    if (action != POPUP_NONE) {
        const char *icon = LV_SYMBOL_SETTINGS;
        const char *text = "";

        switch (action) {

        case POPUP_VOL_UP:
            icon = LV_SYMBOL_VOLUME_MAX;
            text = "VOLUME +";
            break;

        case POPUP_VOL_DOWN:
            icon = LV_SYMBOL_VOLUME_MID;
            text = "VOLUME -";
            break;

        case POPUP_NEXT:
            icon = LV_SYMBOL_NEXT;
            text = "NEXT";
            break;

        case POPUP_PREVIOUS:
            icon = LV_SYMBOL_PREV;
            text = "PREVIOUS";
            break;

        case POPUP_PAGE_UP:
            icon = LV_SYMBOL_UP;
            text = "PAGE UP";
            break;

        case POPUP_PAGE_DOWN:
            icon = LV_SYMBOL_DOWN;
            text = "PAGE DOWN";
            break;
        }

        lv_label_set_text(popup_icon, icon);
        lv_label_set_text(popup_text, text);

        /* Nếu popup chưa hiện thì trượt từ dưới lên */
        if (!popup_visible) {

            lv_obj_clear_flag(popup, LV_OBJ_FLAG_HIDDEN);
            lv_obj_move_foreground(popup);

            lv_obj_set_y(popup, 172);
            lv_obj_set_style_opa(popup, 0, 0);

            popup_animate(
                172,
                116,
                0,
                255,
                180,
                true
            );

            popup_visible = true;
        }

        /* Xoay tiếp thì chỉ kéo dài thời gian hiện,
         * không chạy lại animation liên tục.
         */
        popup_until = now + 650;
    }

    /* Hết thời gian thì trượt xuống */
    if (popup_visible) {
        lv_obj_move_foreground(popup);
    }

    if (popup_visible &&
        (int32_t)(now - popup_until) >= 0) {

        popup_animate(
            lv_obj_get_y(popup),
            172,
            255,
            0,
            160,
            false
        );

        popup_visible = false;
    }
}

static int32_t saver_wave(uint32_t now,
                           uint32_t period,
                           int32_t min_value,
                           int32_t max_value) {
    uint32_t phase = now % period;
    uint32_t half = period / 2U;
    uint32_t pos = phase <= half ? phase : period - phase;

    return min_value +
           (int32_t)(((int64_t)(max_value - min_value) * pos) / half);
}

static void init_screensaver(lv_obj_t *screen) {
    screensaver = lv_obj_create(screen);
    lv_obj_remove_style_all(screensaver);
    lv_obj_set_pos(screensaver, 0, 0);
    lv_obj_set_size(screensaver, 320, 172);
    lv_obj_set_style_bg_color(screensaver, lv_color_hex(0x030407), 0);
    lv_obj_set_style_bg_opa(screensaver, LV_OPA_COVER, 0);
    lv_obj_clear_flag(screensaver, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_add_flag(screensaver, LV_OBJ_FLAG_HIDDEN);

    saver_orb1 = lv_obj_create(screensaver);
    lv_obj_remove_style_all(saver_orb1);
    lv_obj_set_size(saver_orb1, 104, 104);
    lv_obj_set_style_radius(saver_orb1, LV_RADIUS_CIRCLE, 0);
    lv_obj_set_style_bg_color(saver_orb1, lv_color_hex(0x4A7DFF), 0);
    lv_obj_set_style_bg_opa(saver_orb1, 72, 0);

    saver_orb2 = lv_obj_create(screensaver);
    lv_obj_remove_style_all(saver_orb2);
    lv_obj_set_size(saver_orb2, 92, 92);
    lv_obj_set_style_radius(saver_orb2, LV_RADIUS_CIRCLE, 0);
    lv_obj_set_style_bg_color(saver_orb2, lv_color_hex(0xA955FF), 0);
    lv_obj_set_style_bg_opa(saver_orb2, 62, 0);

    saver_glass = lv_obj_create(screensaver);
    lv_obj_remove_style_all(saver_glass);
    lv_obj_set_size(saver_glass, 176, 58);
    lv_obj_align(saver_glass, LV_ALIGN_CENTER, 0, 0);
    lv_obj_set_style_bg_color(saver_glass, lv_color_hex(0x141720), 0);
    lv_obj_set_style_bg_opa(saver_glass, 210, 0);
    lv_obj_set_style_radius(saver_glass, 29, 0);
    lv_obj_set_style_border_width(saver_glass, 1, 0);
    lv_obj_set_style_border_color(saver_glass, lv_color_hex(0xFFFFFF), 0);
    lv_obj_set_style_border_opa(saver_glass, 52, 0);

    saver_title = make_label(saver_glass, &lv_font_montserrat_20);
    lv_label_set_text(saver_title, "LumiPad");
    lv_obj_set_style_text_color(saver_title, lv_color_hex(0xFFFFFF), 0);
    lv_obj_center(saver_title);

}

static void saver_timing_recalculate(void) {
    uint32_t total = 0U;

    for (uint8_t i = 0U; i < saver_media_frame_count; i++) {
        uint16_t interval =
            CLAMP(saver_media_frame_intervals[i],
                  (uint16_t)33U,
                  (uint16_t)5000U);
        saver_media_frame_intervals[i] = interval;
        total += interval;
    }

    saver_media_loop_ms = MAX(total, 1U);

    if (saver_media_frame_count > 0U) {
        saver_media_interval_ms =
            (uint16_t)CLAMP(
                saver_media_loop_ms / saver_media_frame_count,
                33U,
                5000U);
    }
}

static void saver_timing_set_uniform(
    uint8_t frame_count,
    uint16_t interval_ms) {

    interval_ms =
        CLAMP(interval_ms, (uint16_t)33U, (uint16_t)5000U);

    memset(
        saver_media_frame_intervals,
        0,
        sizeof(saver_media_frame_intervals));

    for (uint8_t i = 0U;
         i < frame_count && i < LUMI_SAVER_MAX_FRAMES;
         i++) {
        saver_media_frame_intervals[i] = interval_ms;
    }

    saver_media_loop_ms =
        MAX((uint32_t)frame_count * interval_ms, 1U);
    saver_media_interval_ms = interval_ms;
}

static int saver_flash_open_once(void) {
    if (saver_flash) {
        return 0;
    }

    int rc = flash_area_open(
        FIXED_PARTITION_ID(lumi_saver_partition),
        &saver_flash);

    if (rc != 0 || !saver_flash) {
        saver_flash = NULL;
        lumi_diag_report('E', "Saver flash open rc=%d", rc);
        return rc != 0 ? rc : -ENODEV;
    }

    size_t required =
        SAVER_FLASH_DATA_OFFSET +
        (size_t)LUMI_SAVER_MAX_FRAMES * LUMI_SAVER_FRAME_BYTES;

    if (saver_flash->fa_size < required) {
        lumi_diag_report('E', "Saver flash too small have=%u need=%u",
                         (unsigned int)saver_flash->fa_size,
                         (unsigned int)required);
        return -ENOSPC;
    }

    return 0;
}

static bool saver_flash_load_metadata(void) {
    if (saver_flash_checked) {
        return saver_media_valid;
    }

    saver_flash_checked = true;
    saver_media_valid = false;

    if (saver_flash_open_once() != 0) {
        return false;
    }

    struct saver_flash_header header = {0};
    int read_rc = flash_area_read(saver_flash, 0, &header, sizeof(header));
    if (read_rc != 0) {
        lumi_diag_report('E', "Saver header read rc=%d", read_rc);
        return false;
    }

    if (header.magic != SAVER_FLASH_MAGIC ||
        header.version != SAVER_FLASH_VERSION ||
        header.width != LUMI_SAVER_FRAME_W ||
        header.height != LUMI_SAVER_FRAME_H ||
        header.frame_bytes != LUMI_SAVER_FRAME_BYTES ||
        header.frame_count < 1U ||
        header.frame_count > LUMI_SAVER_MAX_FRAMES ||
        header.interval_ms < 33U ||
        header.data_size !=
            (uint32_t)header.frame_count * LUMI_SAVER_FRAME_BYTES) {
        lumi_diag_report('W', "Saver metadata invalid magic=%08x frames=%u interval=%u",
                         (unsigned int)header.magic,
                         (unsigned int)header.frame_count,
                         (unsigned int)header.interval_ms);
        return false;
    }

    saver_media_frame_count = header.frame_count;
    saver_timing_set_uniform(
        saver_media_frame_count,
        header.interval_ms);

    struct saver_flash_timing timing = {0};
    int timing_rc = flash_area_read(
        saver_flash,
        SAVER_FLASH_TIMING_OFFSET,
        &timing,
        sizeof(timing));

    if (timing_rc == 0 &&
        timing.magic == SAVER_FLASH_TIMING_MAGIC &&
        timing.frame_count == saver_media_frame_count) {

        for (uint8_t i = 0U;
             i < saver_media_frame_count;
             i++) {
            saver_media_frame_intervals[i] =
                timing.interval_ms[i];
        }

        saver_timing_recalculate();
    }

    saver_media_index = 0U;
    saver_media_epoch_ms = 0U;
    saver_media_valid = true;
    lumi_diag_report('I', "Saver metadata OK frames=%u interval=%ums",
                     (unsigned int)saver_media_frame_count,
                     (unsigned int)saver_media_interval_ms);
    return true;
}

static void saver_flash_reset_erase_tracking(void) {
    memset(saver_flash_erased_pages, 0, sizeof(saver_flash_erased_pages));
}

static bool saver_flash_page_is_erased(uint32_t page) {
    if (page >= SAVER_FLASH_MAX_PAGES) {
        return false;
    }

    return (saver_flash_erased_pages[page / 32U] & BIT(page % 32U)) != 0U;
}

static void saver_flash_mark_page_erased(uint32_t page) {
    if (page < SAVER_FLASH_MAX_PAGES) {
        saver_flash_erased_pages[page / 32U] |= BIT(page % 32U);
    }
}

static int saver_flash_erase_page(uint32_t page) {
    if (!saver_flash || page >= SAVER_FLASH_MAX_PAGES) {
        return -EINVAL;
    }

    if (saver_flash_page_is_erased(page)) {
        return 0;
    }

    uint32_t offset = page * SAVER_FLASH_PAGE_SIZE;
    if (offset + SAVER_FLASH_PAGE_SIZE > saver_flash->fa_size) {
        return -ENOSPC;
    }

    int rc = flash_area_erase(
        saver_flash,
        offset,
        SAVER_FLASH_PAGE_SIZE);

    if (rc == 0) {
        saver_flash_mark_page_erased(page);
    }

    return rc;
}

static int saver_flash_prepare_upload(void) {
    int rc = saver_flash_open_once();
    if (rc != 0) {
        return rc;
    }

    saver_flash_reset_erase_tracking();

    /* Erase the metadata page first. Until a new valid header is written at
     * SAVEND, an interrupted upload is intentionally treated as invalid.
     */
    rc = saver_flash_erase_page(0U);
    if (rc != 0) {
        return rc;
    }

    saver_flash_checked = true;
    saver_media_valid = false;
    return 0;
}

static int saver_flash_write_chunk(uint8_t index,
                                   uint16_t offset,
                                   const uint8_t *data,
                                   size_t len) {
    if (!saver_flash || !data || len == 0U) {
        return -EINVAL;
    }

    uint32_t absolute =
        SAVER_FLASH_DATA_OFFSET +
        (uint32_t)index * LUMI_SAVER_FRAME_BYTES +
        offset;

    if ((size_t)absolute + len > saver_flash->fa_size) {
        return -ENOSPC;
    }

    uint32_t first_page = absolute / SAVER_FLASH_PAGE_SIZE;
    uint32_t last_page =
        (uint32_t)(absolute + len - 1U) / SAVER_FLASH_PAGE_SIZE;

    for (uint32_t page = first_page; page <= last_page; page++) {
        int rc = saver_flash_erase_page(page);
        if (rc != 0) {
            return rc;
        }
    }

    return flash_area_write(saver_flash, absolute, data, len);
}

static int saver_flash_read_frame(uint8_t index) {
    if (!saver_media_valid ||
        index >= saver_media_frame_count ||
        saver_flash_open_once() != 0) {
        return -EINVAL;
    }

    uint32_t offset =
        SAVER_FLASH_DATA_OFFSET +
        (uint32_t)index * LUMI_SAVER_FRAME_BYTES;

    return flash_area_read(
        saver_flash,
        offset,
        saver_media_frame_buffer,
        sizeof(saver_media_frame_buffer));
}

static int saver_flash_prefetch_frame(uint8_t index) {
    if (!saver_flash_load_metadata() ||
        index >= saver_media_frame_count ||
        saver_flash_open_once() != 0) {
        saver_prefetch_valid = false;
        return -EINVAL;
    }

    uint32_t offset =
        SAVER_FLASH_DATA_OFFSET +
        (uint32_t)index * LUMI_SAVER_FRAME_BYTES;

    int rc = flash_area_read(
        saver_flash,
        offset,
        saver_media_frame_buffer,
        sizeof(saver_media_frame_buffer));

    if (rc == 0) {
        saver_prefetched_index = index;
        saver_prefetch_valid = true;
    } else {
        saver_prefetch_valid = false;
    }

    return rc;
}

static int saver_flash_commit_header(void) {
    if (!saver_flash) {
        return -ENODEV;
    }

    struct saver_flash_header header = {
        .magic = SAVER_FLASH_MAGIC,
        .version = SAVER_FLASH_VERSION,
        .width = LUMI_SAVER_FRAME_W,
        .height = LUMI_SAVER_FRAME_H,
        .frame_bytes = LUMI_SAVER_FRAME_BYTES,
        .frame_count = saver_media_frame_count,
        .reserved = 0U,
        .interval_ms = saver_media_interval_ms,
        .data_size =
            (uint32_t)saver_media_frame_count * LUMI_SAVER_FRAME_BYTES,
    };

    int rc = flash_area_write(
        saver_flash,
        0,
        &header,
        sizeof(header));

    if (rc != 0) {
        return rc;
    }

    struct saver_flash_timing timing = {
        .magic = SAVER_FLASH_TIMING_MAGIC,
        .frame_count = saver_media_frame_count,
        .reserved0 = 0U,
        .reserved1 = 0U,
        .reserved2 = 0U,
    };

    for (uint8_t i = 0U;
         i < saver_media_frame_count;
         i++) {
        timing.interval_ms[i] =
            saver_media_frame_intervals[i];
    }

    return flash_area_write(
        saver_flash,
        SAVER_FLASH_TIMING_OFFSET,
        &timing,
        sizeof(timing));
}

static void saver_flash_invalidate(void) {
    if (saver_flash_open_once() == 0) {
        saver_flash_reset_erase_tracking();
        (void)saver_flash_erase_page(0U);
    }

    saver_flash_checked = true;
    saver_media_valid = false;
}

static void draw_custom_saver_frame(uint8_t frame_index) {
    if (!device_is_ready(saver_display) ||
        frame_index >= saver_media_frame_count) {
        return;
    }

    if (!saver_prefetch_valid ||
        saver_prefetched_index != frame_index) {
        if (saver_flash_prefetch_frame(frame_index) != 0) {
            return;
        }
    }

    if (!saver_rgb332_lut_ready) {
        for (uint16_t v = 0U; v < 256U; v++) {
            uint8_t r = (uint8_t)((((v >> 5) & 0x07U) * 255U) / 7U);
            uint8_t g = (uint8_t)((((v >> 2) & 0x07U) * 255U) / 7U);
            uint8_t b = (uint8_t)(((v & 0x03U) * 255U) / 3U);
            saver_rgb332_lut[v] = lv_color_make(r, g, b);
        }
        saver_rgb332_lut_ready = true;
    }

    /* Write the 2x image as narrow horizontal stripes instead of asking
     * LVGL to invalidate one zoomed 320x172 object. Total bytes are the same,
     * but each RAMWR window is only up to 8 rows high, so any unsynchronised
     * tear is confined to a much thinner band.
     */
    for (uint16_t src_y = 0U;
         src_y < LUMI_SAVER_FRAME_H;
         src_y += SAVER_STRIPE_SRC_ROWS) {

        uint16_t src_rows =
            MIN((uint16_t)SAVER_STRIPE_SRC_ROWS,
                (uint16_t)(LUMI_SAVER_FRAME_H - src_y));
        uint16_t dst_rows = (uint16_t)(src_rows * 2U);

        for (uint16_t row = 0U; row < src_rows; row++) {
            const uint8_t *src =
                &saver_media_frame_buffer[
                    (size_t)(src_y + row) * LUMI_SAVER_FRAME_W];

            lv_color_t *dst0 =
                &saver_stripe_buf[(size_t)(row * 2U) * 320U];
            lv_color_t *dst1 = dst0 + 320U;

            for (uint16_t x = 0U; x < LUMI_SAVER_FRAME_W; x++) {
                lv_color_t color = saver_rgb332_lut[src[x]];
                dst0[x * 2U] = color;
                dst0[x * 2U + 1U] = color;
                dst1[x * 2U] = color;
                dst1[x * 2U + 1U] = color;
            }
        }

        struct display_buffer_descriptor desc = {
            .buf_size = (size_t)320U * dst_rows * sizeof(lv_color_t),
            .width = 320U,
            .height = dst_rows,
            .pitch = 320U,
        };

        int rc = display_write(
            saver_display,
            0U,
            (uint16_t)(src_y * 2U),
            &desc,
            saver_stripe_buf);

        if (rc != 0) {
            lumi_diag_report('E', "Saver display_write rc=%d y=%u",
                             rc, (unsigned int)(src_y * 2U));
            break;
        }
    }

    saver_prefetch_valid = false;
}

static void refresh_screensaver(lv_timer_t *timer) {
    ARG_UNUSED(timer);

    uint32_t now_uptime = k_uptime_get_32();
    uint32_t delay;
    bool enabled;
    uint8_t style;
    uint32_t color_a;
    uint32_t color_b;
    bool update_wallpaper;
    bool update_saver;

    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    delay = saver_delay_ms;
    enabled = saver_enabled;
    style = saver_style;
    color_a = saver_color_a;
    color_b = saver_color_b;
    update_wallpaper = wallpaper_dirty;
    update_saver = saver_style_dirty;
    wallpaper_dirty = false;
    saver_style_dirty = false;
    k_mutex_unlock(&lumi_ui_config_lock);

    if (update_wallpaper && root_screen) {
        uint32_t wa;
        uint32_t wb;
        k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
        wa = wallpaper_color_a;
        wb = wallpaper_color_b;
        k_mutex_unlock(&lumi_ui_config_lock);

        lv_obj_set_style_bg_color(root_screen, lv_color_hex(wa), 0);
        lv_obj_set_style_bg_grad_color(root_screen, lv_color_hex(wb), 0);
        lv_obj_set_style_bg_grad_dir(root_screen, LV_GRAD_DIR_VER, 0);
    }

    if (update_saver) {
        lv_obj_set_style_bg_color(saver_orb1, lv_color_hex(color_a), 0);
        lv_obj_set_style_bg_color(saver_orb2, lv_color_hex(color_b), 0);

        if (style == LUMI_SAVER_MINIMAL) {
            lv_obj_add_flag(saver_orb1, LV_OBJ_FLAG_HIDDEN);
            lv_obj_add_flag(saver_orb2, LV_OBJ_FLAG_HIDDEN);
            lv_obj_set_style_bg_color(saver_glass, lv_color_hex(0x0F1117), 0);
        } else {
            lv_obj_clear_flag(saver_orb1, LV_OBJ_FLAG_HIDDEN);
            lv_obj_clear_flag(saver_orb2, LV_OBJ_FLAG_HIDDEN);
            lv_obj_set_style_bg_color(saver_glass, lv_color_hex(0x141720), 0);
        }
    }

    bool current_media_active;
    bool current_soft_sleep;

    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    current_media_active = media_active;
    current_soft_sleep = soft_sleep;
    k_mutex_unlock(&lumi_ui_config_lock);

    bool force_show;
    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    force_show = saver_force_show;
    k_mutex_unlock(&lumi_ui_config_lock);

    bool should_show = enabled &&
                       !current_soft_sleep &&
                       saver_media_valid &&
                       (force_show ||
                        (!current_media_active &&
                         delay > 0U &&
                         (uint32_t)(now_uptime - ui_last_activity_ms) >= delay));

    if (should_show && !screensaver_visible) {
        screensaver_visible = true;

        saver_media_index = 0U;
        saver_prefetch_valid = false;
        (void)saver_flash_prefetch_frame(0U);
        saver_media_epoch_ms = lv_tick_get();
        draw_custom_saver_frame(0U);
    } else if (!should_show && screensaver_visible) {
        screensaver_visible = false;

        if (root_screen) {
            lv_obj_invalidate(root_screen);
        }
    }

    if (sleep_overlay) {
        if (current_soft_sleep) {
            lv_obj_clear_flag(sleep_overlay, LV_OBJ_FLAG_HIDDEN);
            lv_obj_move_foreground(sleep_overlay);
        } else {
            lv_obj_add_flag(sleep_overlay, LV_OBJ_FLAG_HIDDEN);
        }
    }

    if (!screensaver_visible) {
        return;
    }

    uint32_t lv_now = lv_tick_get();
    uint32_t elapsed = (uint32_t)(lv_now - saver_media_epoch_ms);
    uint32_t loop_ms = MAX(saver_media_loop_ms, 1U);
    uint32_t loop_pos = elapsed % loop_ms;

    /* Keep playback on the source GIF time line. Slow SPI writes may drop a
     * stale frame, but they must not stretch the loop and make it slow down.
     */
    uint8_t desired_index = 0U;
    uint32_t boundary = 0U;

    for (uint8_t i = 0U; i < saver_media_frame_count; i++) {
        boundary += saver_media_frame_intervals[i];
        desired_index = i;

        if (loop_pos < boundary) {
            break;
        }
    }

    if (desired_index != saver_media_index) {
        saver_media_index = desired_index;
        draw_custom_saver_frame(saver_media_index);
    } else {
        uint8_t next_index =
            (uint8_t)((saver_media_index + 1U) % saver_media_frame_count);

        if (!saver_prefetch_valid ||
            saver_prefetched_index != next_index) {
            (void)saver_flash_prefetch_frame(next_index);
        }
    }

}

bool lumi_ui_saver_anim_begin(uint8_t frame_count, uint16_t frame_interval_ms) {
    if (frame_count < 1U || frame_count > LUMI_SAVER_MAX_FRAMES) {
        return false;
    }

    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    saver_media_valid = false;
    saver_media_frame_count = frame_count;
    saver_media_received_mask = 0U;
    memset(saver_media_received_bytes, 0, sizeof(saver_media_received_bytes));
    saver_timing_set_uniform(
        frame_count,
        frame_interval_ms);
    saver_media_index = 0U;
    saver_media_epoch_ms = 0U;
    k_mutex_unlock(&lumi_ui_config_lock);

    if (saver_flash_prepare_upload() != 0) {
        saver_media_frame_count = 0U;
        return false;
    }

    return true;
}

bool lumi_ui_saver_anim_set_frame_interval(
    uint8_t index,
    uint16_t interval_ms) {

    if (index >= saver_media_frame_count ||
        index >= LUMI_SAVER_MAX_FRAMES) {
        return false;
    }

    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    saver_media_frame_intervals[index] =
        CLAMP(interval_ms, (uint16_t)33U, (uint16_t)5000U);
    saver_timing_recalculate();
    k_mutex_unlock(&lumi_ui_config_lock);

    return true;
}

void lumi_ui_saver_anim_frame(uint8_t index, const uint8_t *data, size_t len) {
    if (!data ||
        index >= saver_media_frame_count ||
        index >= LUMI_SAVER_MAX_FRAMES ||
        len != LUMI_SAVER_FRAME_BYTES) {
        return;
    }

    if (saver_flash_write_chunk(index, 0U, data, len) != 0) {
        return;
    }

    saver_media_received_bytes[index] = LUMI_SAVER_FRAME_BYTES;
    saver_media_received_mask |= BIT(index);
}

bool lumi_ui_saver_anim_chunk(uint8_t index, uint16_t offset,
                              const uint8_t *data, size_t len) {
    if (!data ||
        index >= saver_media_frame_count ||
        index >= LUMI_SAVER_MAX_FRAMES ||
        offset >= LUMI_SAVER_FRAME_BYTES ||
        len == 0U ||
        (size_t)offset + len > LUMI_SAVER_FRAME_BYTES) {
        return false;
    }

    if (saver_flash_write_chunk(index, offset, data, len) != 0) {
        return false;
    }

    uint16_t end = (uint16_t)(offset + len);
    if (end > saver_media_received_bytes[index]) {
        saver_media_received_bytes[index] = end;
    }

    if (saver_media_received_bytes[index] == LUMI_SAVER_FRAME_BYTES) {
        saver_media_received_mask |= BIT(index);
    }

    return true;
}

bool lumi_ui_saver_anim_end(void) {
    uint32_t expected =
        saver_media_frame_count >= 32U
            ? UINT32_MAX
            : ((1U << saver_media_frame_count) - 1U);

    bool ok =
        saver_media_frame_count > 0U &&
        saver_media_received_mask == expected &&
        saver_flash_commit_header() == 0;

    if (ok) {
        saver_media_valid = true;
        saver_media_index = 0U;
        saver_media_epoch_ms = 0U;
        saver_prefetch_valid = false;
    } else {
        saver_media_valid = false;
    }

    lumi_ui_note_activity();
    return ok;
}

bool lumi_ui_saver_anim_is_valid(void) {
    return saver_flash_load_metadata();
}

void lumi_ui_saver_anim_clear(void) {
    saver_flash_invalidate();
    saver_media_frame_count = 0U;
    saver_media_received_mask = 0U;
    memset(saver_media_received_bytes, 0, sizeof(saver_media_received_bytes));
    saver_media_index = 0U;
    saver_prefetch_valid = false;
    lumi_ui_note_activity();
}

static void lumi_panel_refresh_work_handler(struct k_work *work) {
    ARG_UNUSED(work);

    if (sleep_overlay) {
        lv_obj_add_flag(sleep_overlay, LV_OBJ_FLAG_HIDDEN);
    }
    if (root_screen) {
        lv_obj_invalidate(root_screen);
    }
}

K_WORK_DEFINE(lumi_panel_refresh_work, lumi_panel_refresh_work_handler);

static void lumi_panel_sleep_work_handler(struct k_work *work) {
    ARG_UNUSED(work);
    (void)lumi_panel_set_sleep(true);
}

K_WORK_DEFINE(lumi_panel_sleep_work, lumi_panel_sleep_work_handler);

static void lumi_panel_wake_work_handler(struct k_work *work) {
    ARG_UNUSED(work);

    if (lumi_panel_set_sleep(false) == 0) {
        lumi_panel_refresh_work_handler(NULL);
    }
}

K_WORK_DEFINE(lumi_panel_wake_work, lumi_panel_wake_work_handler);

void lumi_ui_note_activity(void) {
    bool was_sleeping;

    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    ui_last_activity_ms = k_uptime_get_32();
    was_sleeping = soft_sleep;
    soft_sleep = false;
    saver_force_show = false;
    k_mutex_unlock(&lumi_ui_config_lock);

    /* Any real input/app activity must re-enable the RGB worker.
     * Soft sleep additionally wakes the ST7789 panel.
     */
    lumi_rgb_set_suspended(false);

    if (was_sleeping) {
        lumi_diag_report('I', "Wake requested by activity");
        k_work_submit_to_queue(zmk_display_work_q(), &lumi_panel_wake_work);
    }
}

void lumi_ui_show_screensaver_now(void) {
    if (!saver_flash_load_metadata()) {
        return;
    }

    bool was_sleeping;

    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    was_sleeping = soft_sleep;
    soft_sleep = false;
    saver_force_show = true;
    k_mutex_unlock(&lumi_ui_config_lock);

    lumi_rgb_set_suspended(false);

    if (was_sleeping) {
        k_work_submit(&lumi_panel_wake_work);
    }
}

void lumi_ui_set_media_active(bool active) {
    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    media_active = active;
    k_mutex_unlock(&lumi_ui_config_lock);

    /* Starting music wakes immediately. Clearing media also resets the
     * inactivity clock so the main menu is shown before the saver returns.
     */
    lumi_ui_note_activity();
}

void lumi_ui_set_wallpaper(uint8_t r1, uint8_t g1, uint8_t b1,
                           uint8_t r2, uint8_t g2, uint8_t b2) {
    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    wallpaper_color_a = ((uint32_t)r1 << 16) | ((uint32_t)g1 << 8) | b1;
    wallpaper_color_b = ((uint32_t)r2 << 16) | ((uint32_t)g2 << 8) | b2;
    wallpaper_dirty = true;
    k_mutex_unlock(&lumi_ui_config_lock);
}

void lumi_ui_set_screensaver(bool enabled, uint8_t style,
                             uint32_t delay_seconds,
                             uint8_t r1, uint8_t g1, uint8_t b1,
                             uint8_t r2, uint8_t g2, uint8_t b2) {
    if (style > LUMI_SAVER_OFF) {
        style = LUMI_SAVER_TAHOE;
    }

    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    saver_enabled = enabled;
    saver_style = style;
    saver_delay_ms = delay_seconds * 1000U;
    saver_color_a = ((uint32_t)r1 << 16) | ((uint32_t)g1 << 8) | b1;
    saver_color_b = ((uint32_t)r2 << 16) | ((uint32_t)g2 << 8) | b2;
    saver_style_dirty = true;
    k_mutex_unlock(&lumi_ui_config_lock);

    lumi_ui_note_activity();
}

void lumi_ui_set_screensaver_delay(uint32_t seconds) {
    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    saver_delay_ms = seconds * 1000U;
    saver_enabled = seconds > 0U;
    k_mutex_unlock(&lumi_ui_config_lock);
    lumi_ui_note_activity();
}

void lumi_ui_set_sleep_timeout(uint32_t seconds) {
    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    sleep_delay_ms = seconds * 1000U;
    k_mutex_unlock(&lumi_ui_config_lock);
    lumi_ui_note_activity();
}

static void lumi_sleep_work_handler(struct k_work *work);
K_WORK_DELAYABLE_DEFINE(lumi_sleep_work, lumi_sleep_work_handler);

static void lumi_sleep_work_handler(struct k_work *work) {
    ARG_UNUSED(work);

    uint32_t timeout;
    bool active_media;
    bool already_sleeping;

    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    timeout = sleep_delay_ms;
    active_media = media_active;
    already_sleeping = soft_sleep;
    k_mutex_unlock(&lumi_ui_config_lock);

    uint32_t now = k_uptime_get_32();

    if (timeout > 0U &&
        !active_media &&
        !already_sleeping &&
        (uint32_t)(now - ui_last_activity_ms) >= timeout) {

        k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
        soft_sleep = true;
        k_mutex_unlock(&lumi_ui_config_lock);

        lumi_diag_report('I', "Entering soft sleep timeout=%us",
                         (unsigned int)(timeout / 1000U));
        lumi_rgb_set_suspended(true);
        k_work_submit_to_queue(zmk_display_work_q(), &lumi_panel_sleep_work);
    }

    k_work_reschedule(&lumi_sleep_work, K_SECONDS(1));
}

lv_obj_t *zmk_display_status_screen(void) {
    (void)lumi_panel_init();
    (void)saver_flash_load_metadata();
    lv_obj_t *screen = lv_obj_create(NULL);
    root_screen = screen;
    ui_last_activity_ms = k_uptime_get_32();
    lv_obj_remove_style_all(screen);
    lv_obj_set_size(screen, 320, 172);
    lv_obj_set_style_bg_color(screen, lv_color_hex(wallpaper_color_a), 0);
    lv_obj_set_style_bg_grad_color(screen, lv_color_hex(wallpaper_color_b), 0);
    lv_obj_set_style_bg_grad_dir(screen, LV_GRAD_DIR_VER, 0);
    lv_obj_set_style_bg_opa(screen, LV_OPA_COVER, 0);
    lv_obj_clear_flag(screen, LV_OBJ_FLAG_SCROLLABLE);
    for (uint8_t i = 0; i < KEY_COUNT; i++) {
        tiles[i] = lv_obj_create(screen);
        lv_obj_remove_style_all(tiles[i]);
        uint8_t col = i % COLS;
uint8_t row = i / COLS;

lv_obj_set_pos(
    tiles[i],
    col * CELL_W,
    STATUS_H + row * CELL_H
);
        lv_obj_set_size(tiles[i], CELL_W, CELL_H);
        lv_obj_set_style_bg_opa(tiles[i], LV_OPA_COVER, 0);
        lv_obj_set_style_border_width(tiles[i], 1, 0);
lv_obj_set_style_border_color(
    tiles[i],
    lv_color_hex(0xD8F8FF),
    0
);

/* Chỉ kẻ đường chia bên trong.
 * Không kẻ viền ngoài màn hình.
 */
lv_border_side_t side = LV_BORDER_SIDE_NONE;

if (col < COLS - 1) {
    side |= LV_BORDER_SIDE_RIGHT;
}

if (row < 2) {
    side |= LV_BORDER_SIDE_BOTTOM;
}

lv_obj_set_style_border_side(
    tiles[i],
    side,
    0
);
        lv_obj_clear_flag(tiles[i], LV_OBJ_FLAG_SCROLLABLE);
        icon_colors[i] = 0xFFFFFF;
        icons[i] = make_label(tiles[i], &lv_font_montserrat_20);
        captions[i] = make_label(tiles[i], &lv_font_montserrat_12);
        lv_obj_add_flag(
    captions[i],
    LV_OBJ_FLAG_HIDDEN
);
        lv_obj_set_width(captions[i], CELL_W - 4);
        lv_label_set_long_mode(captions[i], LV_LABEL_LONG_DOT);
        lv_obj_set_style_text_align(captions[i], LV_TEXT_ALIGN_CENTER, 0);
        lv_obj_align(captions[i], LV_ALIGN_BOTTOM_MID, 0, -3);
    }
    /* Dedicated 28px footer, outside the 144px grid. */
    /* Top status bar */
/* Top status bar */

layer_label = make_label(
    screen,
    &lv_font_montserrat_14
);

lv_obj_set_style_text_letter_space(
    layer_label,
    -1,
    0
);

lv_obj_set_pos(
    layer_label,
    10,
    6
);

lv_obj_set_width(
    layer_label,
    125
);


/* Output */

output_label = make_label(
    screen,
    &lv_font_montserrat_14
);

lv_obj_set_style_text_letter_space(
    output_label,
    -1,
    0
);

lv_obj_set_pos(
    output_label,
    135,
    6
);

lv_obj_set_width(
    output_label,
    100
);

lumi_output_init();


/* Battery */

zmk_widget_battery_status_init(
    &battery_widget,
    screen
);

lv_obj_t *battery =
    zmk_widget_battery_status_obj(
        &battery_widget
    );

lv_obj_set_style_text_font(
    battery,
    &lv_font_montserrat_14,
    0
);

lv_obj_set_style_text_letter_space(
    battery,
    1,
    0
);
    
lv_obj_set_style_text_color(
    battery,
    lv_color_white(),
    0
);

lv_obj_set_width(
    battery,
    82
);


lv_obj_set_style_text_align(
    battery,
    LV_TEXT_ALIGN_RIGHT,
    0
);

lv_obj_set_pos(
    battery,
    226,
    6
);
    
popup = lv_obj_create(screen);
lv_obj_remove_style_all(popup);

/* Pill nhỏ nằm sát phía dưới */
lv_obj_set_pos(popup, 70, 172);
lv_obj_set_size(popup, 180, 44);

/* Nền tối hơi trong */
lv_obj_set_style_bg_color(
    popup,
    lv_color_hex(0x151719),
    0
);

lv_obj_set_style_bg_opa(
    popup,
    235,
    0
);

/* Bo tròn kiểu capsule */
lv_obj_set_style_radius(
    popup,
    22,
    0
);

/* Viền rất nhẹ */
lv_obj_set_style_border_width(
    popup,
    1,
    0
);

lv_obj_set_style_border_color(
    popup,
    lv_color_hex(0xFFFFFF),
    0
);

lv_obj_set_style_border_opa(
    popup,
    55,
    0
);

/* Không cho object bắt touch/click */
lv_obj_clear_flag(
    popup,
    LV_OBJ_FLAG_CLICKABLE
);

/* ICON */
popup_icon = make_label(
    popup,
    &lv_font_montserrat_20
);

lv_obj_set_style_text_color(
    popup_icon,
    lv_color_hex(0xFFFFFF),
    0
);

lv_obj_align(
    popup_icon,
    LV_ALIGN_LEFT_MID,
    14,
    0
);

/* TEXT */
popup_text = make_label(
    popup,
    &lv_font_montserrat_16
);

lv_obj_set_style_text_color(
    popup_text,
    lv_color_hex(0xFFFFFF),
    0
);

lv_obj_set_style_text_letter_space(
    popup_text,
    -1,
    0
);

lv_obj_align(
    popup_text,
    LV_ALIGN_LEFT_MID,
    48,
    0
);

lv_obj_add_flag(
    popup,
    LV_OBJ_FLAG_HIDDEN
);
   lumi_page_init();
    lumi_now_playing_init(screen);
    init_screensaver(screen);

    sleep_overlay = lv_obj_create(screen);
    lv_obj_remove_style_all(sleep_overlay);
    lv_obj_set_pos(sleep_overlay, 0, 0);
    lv_obj_set_size(sleep_overlay, 320, 172);
    lv_obj_set_style_bg_color(sleep_overlay, lv_color_hex(0x000000), 0);
    lv_obj_set_style_bg_opa(sleep_overlay, LV_OPA_COVER, 0);
    lv_obj_clear_flag(sleep_overlay, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_add_flag(sleep_overlay, LV_OBJ_FLAG_HIDDEN);

k_work_schedule(&page_poll_work, K_MSEC(500));

lv_timer_create(refresh_pressed, 20, NULL);
lv_timer_create(refresh_popup, 20, NULL);
lv_timer_create(refresh_screensaver, 10, NULL);

k_work_schedule(&lumi_sleep_work, K_SECONDS(1));

return screen;
}
