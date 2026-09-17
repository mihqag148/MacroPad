/* SPDX-License-Identifier: MIT */

#include <zephyr/device.h>
#include <zephyr/devicetree.h>
#include <zephyr/drivers/led_strip.h>
#include <zephyr/init.h>
#include <zephyr/kernel.h>
#include <zephyr/logging/log.h>

#include <zmk/battery.h>
#include <zmk/keymap.h>
#include <zmk/usb.h>

LOG_MODULE_REGISTER(lumi_rgb, CONFIG_ZMK_LOG_LEVEL);

#if !DT_HAS_CHOSEN(zmk_underglow)
#error "lumi_rgb requires a zmk,underglow chosen node"
#endif

#define STRIP_NODE DT_CHOSEN(zmk_underglow)
#define LED_COUNT DT_PROP(STRIP_NODE, chain_length)
#define FRAME_MS 80
#define MAX_BRIGHTNESS 64 /* ~25% of 255 */

BUILD_ASSERT(LED_COUNT == 4, "Lumi RGB effects expect exactly 4 WS2812B LEDs");

static const struct device *const strip = DEVICE_DT_GET(STRIP_NODE);
static struct led_rgb pixels[LED_COUNT];
static uint8_t rainbow_step;
static uint8_t media_tick;
static uint8_t fusion_tick;

static struct led_rgb scale_rgb(struct led_rgb color, uint8_t scale) {
    color.r = ((uint16_t)color.r * scale) / 255;
    color.g = ((uint16_t)color.g * scale) / 255;
    color.b = ((uint16_t)color.b * scale) / 255;
    return color;
}

static struct led_rgb wheel(uint8_t pos) {
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

    return scale_rgb(color, MAX_BRIGHTNESS);
}

static void fill(struct led_rgb color) {
    for (int i = 0; i < LED_COUNT; i++) {
        pixels[i] = color;
    }
}

static void render_charging(uint8_t soc) {
    fill((struct led_rgb){0});

    if (soc >= 100) {
        fill((struct led_rgb){.r = 0, .g = MAX_BRIGHTNESS, .b = 0});
        return;
    }

    uint8_t lit = (soc + 24) / 25;
    if (lit < 1) {
        lit = 1;
    }
    if (lit > LED_COUNT) {
        lit = LED_COUNT;
    }

    for (int i = 0; i < lit; i++) {
        pixels[i] = (struct led_rgb){.r = MAX_BRIGHTNESS, .g = 0, .b = 0};
    }
}

static void render_office(void) {
    for (int i = 0; i < LED_COUNT; i++) {
        pixels[i] = wheel((uint8_t)(rainbow_step + i * (256 / LED_COUNT)));
    }
    rainbow_step += 4;
}

static void render_media(void) {
    static const uint8_t path[] = {0, 1, 2, 3, 2, 1};
    const struct led_rgb dim_purple = {.r = 10, .g = 0, .b = 8};
    const struct led_rgb warm_purple = {.r = MAX_BRIGHTNESS, .g = 4, .b = 46};

    fill(dim_purple);
    pixels[path[(media_tick / 2) % ARRAY_SIZE(path)]] = warm_purple;
    media_tick++;
}

static void render_fusion360(void) {
    bool on = ((fusion_tick / 6) % 2) == 0;
    const struct led_rgb orange = {.r = MAX_BRIGHTNESS, .g = 22, .b = 0};

    fill(on ? orange : (struct led_rgb){0});
    fusion_tick++;
}

static void lumi_rgb_work_handler(struct k_work *work);
K_WORK_DELAYABLE_DEFINE(lumi_rgb_work, lumi_rgb_work_handler);

static void lumi_rgb_work_handler(struct k_work *work) {
    ARG_UNUSED(work);

    if (!device_is_ready(strip)) {
        k_work_reschedule(&lumi_rgb_work, K_MSEC(500));
        return;
    }

    bool usb_powered = false;
#if IS_ENABLED(CONFIG_USB_DEVICE_STACK)
    usb_powered = zmk_usb_is_powered();
#endif

    if (usb_powered) {
        render_charging(zmk_battery_state_of_charge());
    } else {
        switch ((int)zmk_keymap_highest_layer_active()) {
        case 1:
            render_media();
            break;
        case 2:
            render_fusion360();
            break;
        case 0:
        default:
            render_office();
            break;
        }
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
