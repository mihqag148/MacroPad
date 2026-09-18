/* SPDX-License-Identifier: MIT */

#define DT_DRV_COMPAT zmk_behavior_lumi_rgb

#include <zephyr/device.h>
#include <zephyr/devicetree.h>
#include <zephyr/drivers/led_strip.h>
#include <zephyr/init.h>
#include <zephyr/kernel.h>
#include <zephyr/logging/log.h>

#include <drivers/behavior.h>
#include <zmk/activity.h>
#include <zmk/behavior.h>
#include <zmk/event_manager.h>
#include <zmk/events/activity_state_changed.h>
#include <zmk/events/position_state_changed.h>
#include <zmk/keymap.h>

#include "lumi_rgb.h"

LOG_MODULE_REGISTER(lumi_rgb, CONFIG_ZMK_LOG_LEVEL);

#if !DT_HAS_CHOSEN(zmk_underglow)
#error "lumi_rgb requires a zmk,underglow chosen node"
#endif

#define STRIP_NODE DT_CHOSEN(zmk_underglow)
#define LED_COUNT DT_PROP(STRIP_NODE, chain_length)
#define FRAME_MS 80

#define DEFAULT_BRIGHTNESS 64 /* ~25% */
#define MIN_BRIGHTNESS 13     /* ~5% */
#define MAX_BRIGHTNESS 128    /* ~50% */
#define BRIGHTNESS_STEP 13    /* ~5% */

enum lumi_rgb_command {
    LUMI_RGB_TOGGLE = 0,
    LUMI_RGB_ON = 1,
    LUMI_RGB_OFF = 2,
    LUMI_RGB_BRIGHTER = 3,
    LUMI_RGB_DIMMER = 4,
    LUMI_RGB_NEXT_EFFECT = 5,
    LUMI_RGB_PREV_EFFECT = 6,
    LUMI_RGB_AUTO_LAYER = 7,
};

BUILD_ASSERT(LED_COUNT == 4, "Lumi RGB effects expect exactly 4 WS2812B LEDs");

static const struct device *const strip = DEVICE_DT_GET(STRIP_NODE);
static struct led_rgb pixels[LED_COUNT];

static uint8_t rainbow_step;
static uint8_t media_tick;
static uint8_t fusion_tick;

static bool led_enabled = true;
static bool led_suspended = false;
static bool auto_by_layer = true;
static uint8_t manual_effect = LUMI_RGB_EFFECT_RAINBOW;
static uint8_t user_brightness = DEFAULT_BRIGHTNESS;
static uint8_t user_speed_percent = 50;
static uint8_t reactive_level;
static struct led_rgb manual_color = {.r = 255, .g = 120, .b = 0};

static struct led_rgb scale_rgb(struct led_rgb color, uint8_t scale) {
    color.r = ((uint16_t)color.r * scale) / 255;
    color.g = ((uint16_t)color.g * scale) / 255;
    color.b = ((uint16_t)color.b * scale) / 255;
    return color;
}

static struct led_rgb wheel(uint8_t pos, uint8_t brightness) {
    struct led_rgb color = {0};

    pos = 255 - pos;
    if (pos < 85) {
        color.r = 255 - pos * 3;
        color.g = 0;
        color.b = pos * 3;
    } else if (pos < 170) {
        pos -= 85;
        color.r = 0;
        color.g = pos * 3;
        color.b = 255 - pos * 3;
    } else {
        pos -= 170;
        color.r = pos * 3;
        color.g = 255 - pos * 3;
        color.b = 0;
    }

    return scale_rgb(color, brightness);
}

static void fill(struct led_rgb color) {
    for (int i = 0; i < LED_COUNT; i++) {
        pixels[i] = color;
    }
}

static void render_office(void) {
    for (int i = 0; i < LED_COUNT; i++) {
        pixels[i] = wheel((uint8_t)(rainbow_step + i * (256 / LED_COUNT)), user_brightness);
    }
    rainbow_step += 4;
}

static void render_media(void) {
    static const uint8_t path[] = {0, 1, 2, 3, 2, 1};

    const struct led_rgb dim_purple = {
        .r = user_brightness / 8,
        .g = 0,
        .b = user_brightness / 10,
    };
    const struct led_rgb warm_purple = {
        .r = user_brightness,
        .g = user_brightness / 16,
        .b = (uint8_t)(((uint16_t)user_brightness * 3) / 4),
    };

    fill(dim_purple);
    pixels[path[(media_tick / 2) % ARRAY_SIZE(path)]] = warm_purple;
    media_tick++;
}

static void render_fusion360(void) {
    bool on = ((fusion_tick / 6) % 2) == 0;
    const struct led_rgb orange = {
        .r = user_brightness,
        .g = user_brightness / 3,
        .b = 0,
    };

    fill(on ? orange : (struct led_rgb){0});
    fusion_tick++;
}

static void render_solid(void) {
    fill(scale_rgb(manual_color, user_brightness));
}

static void render_reactive(void) {
    if (reactive_level > 0U) {
        fill(scale_rgb(manual_color,
                       (uint8_t)(((uint16_t)user_brightness * reactive_level) / 255U)));
        reactive_level = reactive_level > 22U ? reactive_level - 22U : 0U;
    } else {
        fill((struct led_rgb){0});
    }
}

static uint8_t layer_effect(void) {
    switch ((int)zmk_keymap_highest_layer_active()) {
    case 1:
        return LUMI_RGB_EFFECT_PURPLE_PINGPONG;
    case 2:
        return LUMI_RGB_EFFECT_ORANGE_BLINK;
    case 0:
    default:
        return LUMI_RGB_EFFECT_RAINBOW;
    }
}

static void render_effect(uint8_t effect) {
    switch (effect) {
    case LUMI_RGB_EFFECT_PURPLE_PINGPONG:
        render_media();
        break;
    case LUMI_RGB_EFFECT_ORANGE_BLINK:
        render_fusion360();
        break;
    case LUMI_RGB_EFFECT_SOLID:
        render_solid();
        break;
    case LUMI_RGB_EFFECT_REACTIVE:
        render_reactive();
        break;
    case LUMI_RGB_EFFECT_RAINBOW:
    default:
        render_office();
        break;
    }
}

static void lumi_rgb_work_handler(struct k_work *work);
K_WORK_DELAYABLE_DEFINE(lumi_rgb_work, lumi_rgb_work_handler);

static void lumi_rgb_refresh_now(void) {
    k_work_reschedule(&lumi_rgb_work, K_NO_WAIT);
}

void lumi_rgb_set_enabled(bool enabled) {
    led_enabled = enabled;
    lumi_rgb_refresh_now();
}

void lumi_rgb_set_brightness_percent(uint8_t percent) {
    if (percent < 5) {
        percent = 5;
    } else if (percent > 50) {
        percent = 50;
    }

    user_brightness = (uint8_t)(((uint16_t)percent * 255U) / 100U);
    lumi_rgb_refresh_now();
}

void lumi_rgb_set_speed_percent(uint8_t percent) {
    if (percent < 10U) {
        percent = 10U;
    } else if (percent > 100U) {
        percent = 100U;
    }

    user_speed_percent = percent;
    lumi_rgb_refresh_now();
}

void lumi_rgb_set_auto(bool enabled) {
    auto_by_layer = enabled;
    if (enabled) {
        led_enabled = true;
    }
    lumi_rgb_refresh_now();
}

void lumi_rgb_set_effect(uint8_t effect) {
    if (effect >= LUMI_RGB_EFFECT_COUNT) {
        return;
    }

    manual_effect = effect;
    auto_by_layer = false;
    led_enabled = true;
    lumi_rgb_refresh_now();
}

void lumi_rgb_set_solid(uint8_t r, uint8_t g, uint8_t b) {
    manual_color = (struct led_rgb){.r = r, .g = g, .b = b};
    manual_effect = LUMI_RGB_EFFECT_SOLID;
    auto_by_layer = false;
    led_enabled = true;
    lumi_rgb_refresh_now();
}

void lumi_rgb_set_suspended(bool suspended) {
    led_suspended = suspended;

    if (suspended && device_is_ready(strip)) {
        fill((struct led_rgb){0});
        led_strip_update_rgb(strip, pixels, LED_COUNT);
    } else if (!suspended) {
        lumi_rgb_refresh_now();
    }
}

static void lumi_rgb_work_handler(struct k_work *work) {
    ARG_UNUSED(work);

    if (!device_is_ready(strip)) {
        k_work_reschedule(&lumi_rgb_work, K_MSEC(500));
        return;
    }

    if (!led_enabled || led_suspended) {
        fill((struct led_rgb){0});
    } else {
        render_effect(auto_by_layer ? layer_effect() : manual_effect);
    }

    int err = led_strip_update_rgb(strip, pixels, LED_COUNT);
    if (err < 0) {
        LOG_ERR("WS2812B update failed: %d", err);
    }

    uint32_t frame_ms = 140U - ((uint32_t)user_speed_percent * 100U / 100U);
    if (frame_ms < 35U) {
        frame_ms = 35U;
    }
    k_work_reschedule(&lumi_rgb_work, K_MSEC(frame_ms));
}

static int lumi_rgb_position_listener(const zmk_event_t *eh) {
    const struct zmk_position_state_changed *event =
        as_zmk_position_state_changed(eh);

    if (event && event->state &&
        !auto_by_layer &&
        manual_effect == LUMI_RGB_EFFECT_REACTIVE) {
        reactive_level = 255U;
    }

    return ZMK_EV_EVENT_BUBBLE;
}

ZMK_LISTENER(lumi_rgb_position, lumi_rgb_position_listener);
ZMK_SUBSCRIPTION(lumi_rgb_position, zmk_position_state_changed);

static int lumi_rgb_activity_listener(const zmk_event_t *eh) {
    const struct zmk_activity_state_changed *event =
        as_zmk_activity_state_changed(eh);

    if (!event || event->state != ZMK_ACTIVITY_SLEEP) {
        return ZMK_EV_EVENT_BUBBLE;
    }

    if (device_is_ready(strip)) {
        fill((struct led_rgb){0});
        led_strip_update_rgb(strip, pixels, LED_COUNT);
    }

    return ZMK_EV_EVENT_BUBBLE;
}

ZMK_LISTENER(lumi_rgb_activity, lumi_rgb_activity_listener);
ZMK_SUBSCRIPTION(lumi_rgb_activity, zmk_activity_state_changed);

static int lumi_rgb_init(void) {
    if (!device_is_ready(strip)) {
        LOG_ERR("WS2812B strip is not ready");
        return -ENODEV;
    }

    fill((struct led_rgb){0});
    led_strip_update_rgb(strip, pixels, LED_COUNT);
    k_work_schedule(&lumi_rgb_work, K_MSEC(250));
    return 0;
}

SYS_INIT(lumi_rgb_init, APPLICATION, 90);

#if DT_HAS_COMPAT_STATUS_OKAY(DT_DRV_COMPAT)

#if IS_ENABLED(CONFIG_ZMK_BEHAVIOR_METADATA)
static const struct behavior_parameter_value_metadata lumi_rgb_commands[] = {
    {.display_name = "Toggle LEDs", .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE, .value = LUMI_RGB_TOGGLE},
    {.display_name = "LEDs On", .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE, .value = LUMI_RGB_ON},
    {.display_name = "LEDs Off", .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE, .value = LUMI_RGB_OFF},
    {.display_name = "Brightness Up", .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE, .value = LUMI_RGB_BRIGHTER},
    {.display_name = "Brightness Down", .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE, .value = LUMI_RGB_DIMMER},
    {.display_name = "Next Effect", .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE, .value = LUMI_RGB_NEXT_EFFECT},
    {.display_name = "Previous Effect", .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE, .value = LUMI_RGB_PREV_EFFECT},
    {.display_name = "Auto by Layer", .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE, .value = LUMI_RGB_AUTO_LAYER},
};

static const struct behavior_parameter_metadata_set lumi_rgb_metadata_sets[] = {
    {
        .param1_values = lumi_rgb_commands,
        .param1_values_len = ARRAY_SIZE(lumi_rgb_commands),
    },
};

static const struct behavior_parameter_metadata lumi_rgb_metadata = {
    .sets_len = ARRAY_SIZE(lumi_rgb_metadata_sets),
    .sets = lumi_rgb_metadata_sets,
};
#endif

static int lumi_rgb_binding_pressed(struct zmk_behavior_binding *binding,
                                    struct zmk_behavior_binding_event event) {
    ARG_UNUSED(event);

    switch (binding->param1) {
    case LUMI_RGB_TOGGLE:
        led_enabled = !led_enabled;
        break;
    case LUMI_RGB_ON:
        lumi_rgb_set_enabled(true);
        break;
    case LUMI_RGB_OFF:
        lumi_rgb_set_enabled(false);
        break;
    case LUMI_RGB_BRIGHTER:
        if (user_brightness < MAX_BRIGHTNESS) {
            uint16_t next = user_brightness + BRIGHTNESS_STEP;
            user_brightness = next > MAX_BRIGHTNESS ? MAX_BRIGHTNESS : (uint8_t)next;
        }
        break;
    case LUMI_RGB_DIMMER:
        if (user_brightness > MIN_BRIGHTNESS) {
            int next = user_brightness - BRIGHTNESS_STEP;
            user_brightness = next < MIN_BRIGHTNESS ? MIN_BRIGHTNESS : (uint8_t)next;
        }
        break;
    case LUMI_RGB_NEXT_EFFECT:
        if (auto_by_layer) {
            manual_effect = layer_effect();
        }
        manual_effect = (manual_effect + 1) % LUMI_RGB_EFFECT_COUNT;
        auto_by_layer = false;
        led_enabled = true;
        break;
    case LUMI_RGB_PREV_EFFECT:
        if (auto_by_layer) {
            manual_effect = layer_effect();
        }
        manual_effect = (manual_effect + LUMI_RGB_EFFECT_COUNT - 1) % LUMI_RGB_EFFECT_COUNT;
        auto_by_layer = false;
        led_enabled = true;
        break;
    case LUMI_RGB_AUTO_LAYER:
        lumi_rgb_set_auto(true);
        break;
    default:
        return -ENOTSUP;
    }

    return ZMK_BEHAVIOR_OPAQUE;
}

static int lumi_rgb_binding_released(struct zmk_behavior_binding *binding,
                                     struct zmk_behavior_binding_event event) {
    ARG_UNUSED(binding);
    ARG_UNUSED(event);
    return ZMK_BEHAVIOR_OPAQUE;
}

static const struct behavior_driver_api lumi_rgb_behavior_driver_api = {
    .binding_pressed = lumi_rgb_binding_pressed,
    .binding_released = lumi_rgb_binding_released,
    .locality = BEHAVIOR_LOCALITY_GLOBAL,
#if IS_ENABLED(CONFIG_ZMK_BEHAVIOR_METADATA)
    .parameter_metadata = &lumi_rgb_metadata,
#endif
};

BEHAVIOR_DT_INST_DEFINE(0, NULL, NULL, NULL, NULL, POST_KERNEL,
                        CONFIG_KERNEL_INIT_PRIORITY_DEFAULT, &lumi_rgb_behavior_driver_api);

#endif
