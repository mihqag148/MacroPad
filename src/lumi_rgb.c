/* SPDX-License-Identifier: MIT */

#define DT_DRV_COMPAT zmk_behavior_lumi_rgb

#include <zephyr/device.h>
#include <zephyr/devicetree.h>
#include <zephyr/drivers/led_strip.h>
#include <zephyr/init.h>
#include <zephyr/kernel.h>
#include <zephyr/logging/log.h>

#include <drivers/behavior.h>
#include <zmk/behavior.h>
#include <zmk/keymap.h>

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

enum lumi_rgb_effect {
    LUMI_EFFECT_RAINBOW = 0,
    LUMI_EFFECT_PURPLE_PINGPONG = 1,
    LUMI_EFFECT_ORANGE_BLINK = 2,
    LUMI_EFFECT_COUNT,
};

BUILD_ASSERT(LED_COUNT == 4, "Lumi RGB effects expect exactly 4 WS2812B LEDs");

static const struct device *const strip = DEVICE_DT_GET(STRIP_NODE);
static struct led_rgb pixels[LED_COUNT];

static uint8_t rainbow_step;
static uint8_t media_tick;
static uint8_t fusion_tick;

static bool led_enabled = true;
static bool auto_by_layer = true;
static uint8_t manual_effect = LUMI_EFFECT_RAINBOW;
static uint8_t user_brightness = DEFAULT_BRIGHTNESS;

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

static uint8_t layer_effect(void) {
    switch ((int)zmk_keymap_highest_layer_active()) {
    case 1:
        return LUMI_EFFECT_PURPLE_PINGPONG;
    case 2:
        return LUMI_EFFECT_ORANGE_BLINK;
    case 0:
    default:
        return LUMI_EFFECT_RAINBOW;
    }
}

static void render_effect(uint8_t effect) {
    switch (effect) {
    case LUMI_EFFECT_PURPLE_PINGPONG:
        render_media();
        break;
    case LUMI_EFFECT_ORANGE_BLINK:
        render_fusion360();
        break;
    case LUMI_EFFECT_RAINBOW:
    default:
        render_office();
        break;
    }
}

static void lumi_rgb_work_handler(struct k_work *work);
K_WORK_DELAYABLE_DEFINE(lumi_rgb_work, lumi_rgb_work_handler);

static void lumi_rgb_work_handler(struct k_work *work) {
    ARG_UNUSED(work);

    if (!device_is_ready(strip)) {
        k_work_reschedule(&lumi_rgb_work, K_MSEC(500));
        return;
    }

    if (!led_enabled) {
        fill((struct led_rgb){0});
    } else {
        render_effect(auto_by_layer ? layer_effect() : manual_effect);
    }

    int err = led_strip_update_rgb(strip, pixels, LED_COUNT);
    if (err < 0) {
        LOG_ERR("WS2812B update failed: %d", err);
    }

    k_work_reschedule(&lumi_rgb_work, K_MSEC(FRAME_MS));
}

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
    {
        .display_name = "Toggle LEDs",
        .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE,
        .value = LUMI_RGB_TOGGLE,
    },
    {
        .display_name = "LEDs On",
        .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE,
        .value = LUMI_RGB_ON,
    },
    {
        .display_name = "LEDs Off",
        .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE,
        .value = LUMI_RGB_OFF,
    },
    {
        .display_name = "Brightness Up",
        .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE,
        .value = LUMI_RGB_BRIGHTER,
    },
    {
        .display_name = "Brightness Down",
        .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE,
        .value = LUMI_RGB_DIMMER,
    },
    {
        .display_name = "Next Effect",
        .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE,
        .value = LUMI_RGB_NEXT_EFFECT,
    },
    {
        .display_name = "Previous Effect",
        .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE,
        .value = LUMI_RGB_PREV_EFFECT,
    },
    {
        .display_name = "Auto by Layer",
        .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE,
        .value = LUMI_RGB_AUTO_LAYER,
    },
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
        led_enabled = true;
        break;
    case LUMI_RGB_OFF:
        led_enabled = false;
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
        manual_effect = (manual_effect + 1) % LUMI_EFFECT_COUNT;
        auto_by_layer = false;
        led_enabled = true;
        break;
    case LUMI_RGB_PREV_EFFECT:
        if (auto_by_layer) {
            manual_effect = layer_effect();
        }
        manual_effect = (manual_effect + LUMI_EFFECT_COUNT - 1) % LUMI_EFFECT_COUNT;
        auto_by_layer = false;
        led_enabled = true;
        break;
    case LUMI_RGB_AUTO_LAYER:
        auto_by_layer = true;
        led_enabled = true;
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
