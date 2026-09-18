/* SPDX-License-Identifier: MIT
 * Lumi MacroPad: four columns, three rows, live ZMK keymap captions.
 */
#include <stdio.h>
#include <string.h>
#include <zephyr/device.h>
#include <zephyr/devicetree.h>
#include <zephyr/drivers/display.h>
#include <zephyr/kernel.h>
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
static bool screensaver_visible;
static lv_obj_t *root_screen;

K_MUTEX_DEFINE(lumi_ui_config_lock);
static uint32_t ui_last_activity_ms;
static uint32_t saver_delay_ms = 60000U;
static uint32_t sleep_delay_ms = 120000U;
static bool saver_enabled = true;
static uint8_t saver_style = LUMI_SAVER_TAHOE;
static uint32_t wallpaper_color_a = 0x000000;
static uint32_t wallpaper_color_b = 0x10151F;
static uint32_t saver_color_a = 0x4A7DFF;
static uint32_t saver_color_b = 0xA955FF;
static bool wallpaper_dirty = true;
static bool saver_style_dirty = true;

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
    previous = state;
    have_previous = true;
    const uint32_t colors[] = {0x5de0c3, 0xffc765, 0x7ebcff};
    accent = lv_color_hex(colors[state.id % ARRAY_SIZE(colors)]);
    lv_label_set_text(layer_label, state.name);
    lv_obj_set_style_text_color(layer_label, accent, 0);
    for (uint8_t i = 0; i < KEY_COUNT; i++) {
        lv_label_set_text(captions[i], state.keys[i].text);
        lv_label_set_text(icons[i], state.keys[i].icon);
        icon_colors[i] = state.keys[i].color;
        set_tile_pressed(i, highlighted[i]);
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
        lumi_now_playing_user_activity();
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

    } else if (keycode_matches(event, C_PREVIOUS)) {
        atomic_set(&popup_action, POPUP_PREVIOUS);

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

    bool should_show = enabled &&
                       style != LUMI_SAVER_OFF &&
                       delay > 0U &&
                       (uint32_t)(now_uptime - ui_last_activity_ms) >= delay;

    if (should_show && !screensaver_visible) {
        screensaver_visible = true;
        lv_obj_clear_flag(screensaver, LV_OBJ_FLAG_HIDDEN);
        lv_obj_move_foreground(screensaver);
        lv_obj_set_style_opa(screensaver, 0, 0);

        lv_anim_t a;
        lv_anim_init(&a);
        lv_anim_set_var(&a, screensaver);
        lv_anim_set_exec_cb(&a, popup_anim_opa_cb);
        lv_anim_set_values(&a, 0, 255);
        lv_anim_set_time(&a, 420);
        lv_anim_set_path_cb(&a, lv_anim_path_ease_out);
        lv_anim_start(&a);
    } else if (!should_show && screensaver_visible) {
        screensaver_visible = false;
        lv_anim_del(screensaver, popup_anim_opa_cb);
        lv_obj_set_style_opa(screensaver, LV_OPA_COVER, 0);
        lv_obj_add_flag(screensaver, LV_OBJ_FLAG_HIDDEN);
    }

    if (!screensaver_visible) {
        return;
    }

    uint32_t lv_now = lv_tick_get();

    if (style == LUMI_SAVER_TAHOE) {
        lv_obj_set_pos(
            saver_orb1,
            saver_wave(lv_now, 7200, -30, 72),
            saver_wave(lv_now + 1700, 6100, -28, 52)
        );

        lv_obj_set_pos(
            saver_orb2,
            saver_wave(lv_now + 2600, 8300, 204, 256),
            saver_wave(lv_now + 900, 6900, 58, 112)
        );

        lv_obj_set_y(
            saver_glass,
            saver_wave(lv_now, 9000, 54, 60)
        );
    } else {
        lv_obj_align(saver_glass, LV_ALIGN_CENTER, 0, 0);
    }

    lv_obj_move_foreground(screensaver);
}

void lumi_ui_note_activity(void) {
    ui_last_activity_ms = k_uptime_get_32();
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
    k_mutex_lock(&lumi_ui_config_lock, K_FOREVER);
    timeout = sleep_delay_ms;
    k_mutex_unlock(&lumi_ui_config_lock);

    uint32_t now = k_uptime_get_32();
    bool usb_powered = zmk_usb_is_powered();

    if (timeout > 0U &&
        !usb_powered &&
        (uint32_t)(now - ui_last_activity_ms) >= timeout) {
        lumi_rgb_prepare_sleep();

        const struct device *display = DEVICE_DT_GET(DT_CHOSEN(zephyr_display));
        if (device_is_ready(display)) {
            display_blanking_on(display);
        }

        if (zmk_pm_suspend_devices() >= 0) {
            sys_poweroff();
        }
    }

    k_work_reschedule(&lumi_sleep_work, K_SECONDS(1));
}

lv_obj_t *zmk_display_status_screen(void) {
    (void)lumi_panel_init();
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
k_work_schedule(&page_poll_work, K_MSEC(500));

lv_timer_create(refresh_pressed, 20, NULL);
lv_timer_create(refresh_popup, 20, NULL);
lv_timer_create(refresh_screensaver, 50, NULL);
k_work_schedule(&lumi_sleep_work, K_SECONDS(1));

return screen;
}
