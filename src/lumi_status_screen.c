#include <stdio.h>
#include <string.h>

#include <zephyr/kernel.h>

#include <lvgl.h>

#include <zmk/display.h>
#include <zmk/display/status_screen.h>
#include <zmk/display/widgets/battery_status.h>
#include <zmk/display/widgets/output_status.h>

#include <zmk/events/layer_state_changed.h>
#include <zmk/event_manager.h>
#include <zmk/keymap.h>

static struct zmk_widget_battery_status battery_widget;
static struct zmk_widget_output_status output_widget;

static lv_obj_t *layer_label;
static lv_obj_t *footer_label;
static lv_obj_t *splash_label;

struct lumi_layer_state {
    zmk_keymap_layer_index_t index;
    const char *name;
};

static void update_layer(struct lumi_layer_state state) {
    if (layer_label == NULL) {
        return;
    }

    char text[32];

    if (state.name != NULL && strlen(state.name) > 0) {
        snprintf(text, sizeof(text), "%s", state.name);
    } else {
        snprintf(text, sizeof(text), "LAYER %d", state.index);
    }

    lv_label_set_text(layer_label, text);
}

static struct lumi_layer_state get_layer_state(const zmk_event_t *eh) {
    zmk_keymap_layer_index_t index = zmk_keymap_highest_layer_active();

    return (struct lumi_layer_state){
        .index = index,
        .name = zmk_keymap_layer_name(
            zmk_keymap_layer_index_to_id(index)
        ),
    };
}

ZMK_DISPLAY_WIDGET_LISTENER(
    lumi_layer,
    struct lumi_layer_state,
    update_layer,
    get_layer_state
)

ZMK_SUBSCRIPTION(lumi_layer, zmk_layer_state_changed);

static void splash_done(lv_timer_t *timer) {
    if (splash_label != NULL) {
        lv_obj_del(splash_label);
        splash_label = NULL;
    }

    lv_obj_clear_flag(layer_label, LV_OBJ_FLAG_HIDDEN);
    lv_obj_clear_flag(footer_label, LV_OBJ_FLAG_HIDDEN);

#if IS_ENABLED(CONFIG_ZMK_WIDGET_BATTERY_STATUS)
    lv_obj_clear_flag(
        zmk_widget_battery_status_obj(&battery_widget),
        LV_OBJ_FLAG_HIDDEN
    );
#endif

#if IS_ENABLED(CONFIG_ZMK_WIDGET_OUTPUT_STATUS)
    lv_obj_clear_flag(
        zmk_widget_output_status_obj(&output_widget),
        LV_OBJ_FLAG_HIDDEN
    );
#endif

    lv_timer_del(timer);
}

lv_obj_t *zmk_display_status_screen() {
    lv_obj_t *screen = lv_obj_create(NULL);

    lv_obj_set_style_bg_color(
        screen,
        lv_color_white(),
        LV_PART_MAIN
    );

    lv_obj_set_style_text_color(
        screen,
        lv_color_black(),
        LV_PART_MAIN
    );

    /* -------- Main layer name -------- */

    layer_label = lv_label_create(screen);

    lv_obj_set_width(layer_label, 300);

    lv_obj_set_style_text_font(
        layer_label,
        &lv_font_montserrat_32,
        LV_PART_MAIN
    );

    lv_obj_set_style_text_align(
        layer_label,
        LV_TEXT_ALIGN_CENTER,
        LV_PART_MAIN
    );

    lv_obj_align(
        layer_label,
        LV_ALIGN_CENTER,
        0,
        -5
    );

    lumi_layer_init();

    /* -------- Battery -------- */

#if IS_ENABLED(CONFIG_ZMK_WIDGET_BATTERY_STATUS)
    zmk_widget_battery_status_init(
        &battery_widget,
        screen
    );

    lv_obj_align(
        zmk_widget_battery_status_obj(&battery_widget),
        LV_ALIGN_TOP_RIGHT,
        -8,
        8
    );
#endif

    /* -------- USB / BLE -------- */

#if IS_ENABLED(CONFIG_ZMK_WIDGET_OUTPUT_STATUS)
    zmk_widget_output_status_init(
        &output_widget,
        screen
    );

    lv_obj_align(
        zmk_widget_output_status_obj(&output_widget),
        LV_ALIGN_TOP_LEFT,
        8,
        8
    );
#endif

    /* -------- Footer -------- */

    footer_label = lv_label_create(screen);

    lv_label_set_text(
        footer_label,
        "LUMI MACROPAD"
    );

    lv_obj_set_style_text_font(
        footer_label,
        &lv_font_montserrat_16,
        LV_PART_MAIN
    );

    lv_obj_align(
        footer_label,
        LV_ALIGN_BOTTOM_MID,
        0,
        -8
    );

    /* Hide main UI while splash is visible */

    lv_obj_add_flag(layer_label, LV_OBJ_FLAG_HIDDEN);
    lv_obj_add_flag(footer_label, LV_OBJ_FLAG_HIDDEN);

#if IS_ENABLED(CONFIG_ZMK_WIDGET_BATTERY_STATUS)
    lv_obj_add_flag(
        zmk_widget_battery_status_obj(&battery_widget),
        LV_OBJ_FLAG_HIDDEN
    );
#endif

#if IS_ENABLED(CONFIG_ZMK_WIDGET_OUTPUT_STATUS)
    lv_obj_add_flag(
        zmk_widget_output_status_obj(&output_widget),
        LV_OBJ_FLAG_HIDDEN
    );
#endif

    /* -------- Splash screen -------- */

    splash_label = lv_label_create(screen);

    lv_label_set_text(
        splash_label,
        "LUMI3D"
    );

    lv_obj_set_style_text_font(
        splash_label,
        &lv_font_montserrat_36,
        LV_PART_MAIN
    );

    lv_obj_center(splash_label);

    /* 1500 ms splash */

    lv_timer_create(
        splash_done,
        1500,
        NULL
    );

    return screen;
}
