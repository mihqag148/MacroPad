/* SPDX-License-Identifier: MIT
 * Lumi MacroPad: four columns, three rows, live ZMK keymap captions.
 */
#include <stdio.h>
#include <string.h>
#include <zephyr/kernel.h>
#include <zephyr/sys/atomic.h>
#include <lvgl.h>
#include <dt-bindings/zmk/keys.h>
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
#include "lumi_panel.h"

#define KEY_COUNT 12
#define COLS 4
#define CELL_W 80
#define CELL_H 48
#define PRESS_MIN_MS 100

static lv_obj_t *tiles[KEY_COUNT], *icons[KEY_COUNT], *captions[KEY_COUNT];
static lv_obj_t *layer_label, *output_label;
static struct zmk_widget_battery_status battery_widget;
static atomic_t held_keys, tapped_keys;
static atomic_t popup_action;

static lv_obj_t *popup;
static lv_obj_t *popup_icon;
static lv_obj_t *popup_text;

static uint32_t popup_until;

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
static lv_color_t accent;

/* Slots are ZMK positions 0..11 in order. Position 12 is the encoder.
 * The wired 4x4 matrix and its transform remain unchanged.
 */
struct key_caption {
    char text[16];
    const char *icon;
};
struct page_state {
    zmk_keymap_layer_id_t id;
    char name[32];
    struct key_caption keys[KEY_COUNT];
};

static void describe_key(uint32_t code, struct key_caption *out) {
    const char *text = NULL;
    out->icon = LV_SYMBOL_KEYBOARD;
    switch (code) {
    case LC(C): text = "COPY"; out->icon = LV_SYMBOL_COPY; break;
    case LC(V): text = "PASTE"; out->icon = LV_SYMBOL_PASTE; break;
    case LC(X): text = "CUT"; out->icon = LV_SYMBOL_CUT; break;
    case LC(Z): text = "UNDO"; out->icon = LV_SYMBOL_LEFT; break;
    case LC(Y): text = "REDO"; out->icon = LV_SYMBOL_RIGHT; break;
    case LC(A): text = "SELECT ALL"; out->icon = LV_SYMBOL_LIST; break;
    case LC(S): text = "SAVE"; out->icon = LV_SYMBOL_SAVE; break;
    case LC(F): text = "FIND"; out->icon = LV_SYMBOL_EYE_OPEN; break;
    case ENTER: text = "ENTER"; out->icon = LV_SYMBOL_OK; break;
    case BACKSPACE: text = "BACKSPACE"; out->icon = LV_SYMBOL_BACKSPACE; break;
    case TAB: text = "TAB"; out->icon = LV_SYMBOL_RIGHT; break;
    case ESC: text = "ESC"; out->icon = LV_SYMBOL_CLOSE; break;
    case DELETE: text = "DELETE"; out->icon = LV_SYMBOL_TRASH; break;
    case C_PLAY_PAUSE: text = "PLAY/PAUSE"; out->icon = LV_SYMBOL_PLAY; break;
    case C_PREVIOUS: text = "PREVIOUS"; out->icon = LV_SYMBOL_PREV; break;
    case C_NEXT: text = "NEXT"; out->icon = LV_SYMBOL_NEXT; break;
    case C_STOP: text = "STOP"; out->icon = LV_SYMBOL_STOP; break;
    case C_MUTE: text = "MUTE"; out->icon = LV_SYMBOL_MUTE; break;
    case C_VOL_DN: text = "VOL -"; out->icon = LV_SYMBOL_VOLUME_MID; break;
    case C_VOL_UP: text = "VOL +"; out->icon = LV_SYMBOL_VOLUME_MAX; break;
    case C_AC_BACK: text = "BACK"; out->icon = LV_SYMBOL_LEFT; break;
    case C_AC_FORWARD: text = "FORWARD"; out->icon = LV_SYMBOL_RIGHT; break;
    case HOME: text = "HOME"; out->icon = LV_SYMBOL_HOME; break;
    case END: text = "END"; out->icon = LV_SYMBOL_DOWN; break;
    case PG_UP: text = "PAGE UP"; out->icon = LV_SYMBOL_UP; break;
    case PG_DN: text = "PAGE DOWN"; out->icon = LV_SYMBOL_DOWN; break;
    default: break;
    }
    if (text) {
        snprintf(out->text, sizeof(out->text), "%s", text);
    } else if (STRIP_MODS(code) >= A && STRIP_MODS(code) <= Z) {
        /* Keep Fusion F/V/M/E/L honest after Studio edits: show actual keys. */
        snprintf(out->text, sizeof(out->text), "%s%s%s%s%c",
                 SELECT_MODS(code) & (MOD_LCTL | MOD_RCTL) ? "C+" : "",
                 SELECT_MODS(code) & (MOD_LALT | MOD_RALT) ? "A+" : "",
                 SELECT_MODS(code) & (MOD_LSFT | MOD_RSFT) ? "S+" : "",
                 SELECT_MODS(code) & (MOD_LGUI | MOD_RGUI) ? "W+" : "",
                 (char)('A' + STRIP_MODS(code) - A));
    } else {
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

    lv_obj_set_style_border_color(
        tiles[i],
        pressed ? lv_color_hex(0x7EEBFF) : lv_color_hex(0xD8F8FF),
        0
    );

    lv_obj_set_style_text_color(
        icons[i],
        lv_color_hex(0xD8F8FF),
        0
    );

    lv_obj_set_style_text_color(
        captions[i],
        lv_color_hex(0xFFFFFF),
        0
    );

    lv_obj_align(
        icons[i],
        LV_ALIGN_TOP_MID,
        0,
        pressed ? 8 : 3
    );
}

static void update_page(struct page_state state) {
    static struct page_state previous;
    static bool have_previous;
    bool same = have_previous && previous.id == state.id &&
                strcmp(previous.name, state.name) == 0;
    for (uint8_t i = 0; same && i < KEY_COUNT; i++) {
        same = previous.keys[i].icon == state.keys[i].icon &&
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

    if (keycode_matches(event, C_VOL_UP)) {
        atomic_set(&popup_action, POPUP_VOL_UP);

    } else if (keycode_matches(event, C_VOL_DN)) {
        atomic_set(&popup_action, POPUP_VOL_DOWN);

    } else if (keycode_matches(event, C_NEXT)) {
        atomic_set(&popup_action, POPUP_NEXT);

    } else if (keycode_matches(event, C_PREVIOUS)) {
        atomic_set(&popup_action, POPUP_PREVIOUS);

    } else if (keycode_matches(event, PG_UP)) {
        atomic_set(&popup_action, POPUP_PAGE_UP);

    } else if (keycode_matches(event, PG_DN)) {
        atomic_set(&popup_action, POPUP_PAGE_DOWN);
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
            text = "NEXT TRACK";
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

        lv_obj_clear_flag(popup, LV_OBJ_FLAG_HIDDEN);
        lv_obj_move_foreground(popup);

        popup_until = now + 800;
    }

    if (!lv_obj_has_flag(popup, LV_OBJ_FLAG_HIDDEN) &&
        (int32_t)(now - popup_until) >= 0) {

        lv_obj_add_flag(popup, LV_OBJ_FLAG_HIDDEN);
    }
}

lv_obj_t *zmk_display_status_screen(void) {
    (void)lumi_panel_init();
    lv_obj_t *screen = lv_obj_create(NULL);
    lv_obj_remove_style_all(screen);
    lv_obj_set_size(screen, 320, 172);
    lv_obj_set_style_bg_color(screen, lv_color_hex(0x000000), 0);
    lv_obj_set_style_bg_opa(screen, LV_OPA_COVER, 0);
    lv_obj_clear_flag(screen, LV_OBJ_FLAG_SCROLLABLE);
    for (uint8_t i = 0; i < KEY_COUNT; i++) {
        tiles[i] = lv_obj_create(screen);
        lv_obj_remove_style_all(tiles[i]);
        lv_obj_set_pos(tiles[i], (i % COLS) * CELL_W, (i / COLS) * CELL_H);
        lv_obj_set_size(tiles[i], CELL_W, CELL_H);
        lv_obj_set_style_bg_opa(tiles[i], LV_OPA_COVER, 0);
        lv_obj_set_style_border_width(tiles[i], 1, 0);
        lv_obj_clear_flag(tiles[i], LV_OBJ_FLAG_SCROLLABLE);
        icons[i] = make_label(tiles[i], &lv_font_montserrat_20);
        captions[i] = make_label(tiles[i], &lv_font_montserrat_12);
        lv_obj_set_width(captions[i], CELL_W - 4);
        lv_label_set_long_mode(captions[i], LV_LABEL_LONG_DOT);
        lv_obj_set_style_text_align(captions[i], LV_TEXT_ALIGN_CENTER, 0);
        lv_obj_align(captions[i], LV_ALIGN_BOTTOM_MID, 0, -3);
    }
    /* Dedicated 28px footer, outside the 144px grid. */
    layer_label = make_label(screen, &lv_font_montserrat_12);
    lv_obj_set_pos(layer_label, 5, 152);
    lv_obj_set_width(layer_label, 144);
    lv_label_set_long_mode(layer_label, LV_LABEL_LONG_DOT);
    output_label = make_label(screen, &lv_font_montserrat_12);
    lv_obj_set_pos(output_label, 157, 152);
    lv_obj_set_width(output_label, 83);
    lumi_output_init();
    zmk_widget_battery_status_init(&battery_widget, screen);
    lv_obj_t *battery = zmk_widget_battery_status_obj(&battery_widget);
    lv_obj_set_style_text_font(battery, &lv_font_montserrat_12, 0);
    lv_obj_set_style_text_color(battery, lv_color_white(), 0);
    lv_obj_set_width(battery, 70);
    lv_obj_set_style_text_align(battery, LV_TEXT_ALIGN_RIGHT, 0);
    lv_obj_set_pos(battery, 245, 152);
    lumi_page_init();
    k_work_schedule(&page_poll_work, K_MSEC(500));
    lv_timer_create(refresh_pressed, 20, NULL);
    return screen;
}
