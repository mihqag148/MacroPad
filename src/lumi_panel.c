/* SPDX-License-Identifier: MIT */
#include <errno.h>
#include <zephyr/device.h>
#include <zephyr/drivers/gpio.h>
#include <zephyr/drivers/spi.h>
#include <zephyr/logging/log.h>
#include <zephyr/kernel.h>
#include "lumi_panel.h"
#include "lumi_diag.h"

LOG_MODULE_REGISTER(lumi_panel, CONFIG_ZMK_LOG_LEVEL);
#define PANEL DT_CHOSEN(zephyr_display)
static const struct spi_dt_spec bus =
    SPI_DT_SPEC_GET(PANEL, SPI_OP_MODE_MASTER | SPI_WORD_SET(8), 0);
static const struct gpio_dt_spec dc = GPIO_DT_SPEC_GET(PANEL, cmd_data_gpios);

/* ST7789 module BLK/backlight control on P0.08. */
#define LUMI_BL_PIN 8U
static const struct device *const bl_gpio =
    DEVICE_DT_GET(DT_NODELABEL(gpio0));
static bool bl_ready;

static int lumi_panel_backlight_init(void) {
    if (!device_is_ready(bl_gpio)) {
        lumi_diag_report('E', "Backlight GPIO not ready");
        return -ENODEV;
    }

    int err = gpio_pin_configure(
        bl_gpio,
        LUMI_BL_PIN,
        GPIO_OUTPUT_LOW);
    if (err) {
        lumi_diag_report('E', "Backlight GPIO init rc=%d", err);
        return err;
    }

    bl_ready = true;
    return 0;
}

int lumi_panel_set_backlight(bool enabled) {
    if (!bl_ready) {
        int err = lumi_panel_backlight_init();
        if (err) {
            return err;
        }
    }

    int err = gpio_pin_set(
        bl_gpio,
        LUMI_BL_PIN,
        enabled ? 1 : 0);
    if (err) {
        lumi_diag_report(
            'E',
            "Backlight %s rc=%d",
            enabled ? "ON" : "OFF",
            err);
        return err;
    }

    lumi_diag_report(
        'I',
        "Backlight %s",
        enabled ? "ON" : "OFF");
    return 0;
}

int lumi_panel_init(void) {
    /* Keep the backlight dark until the complete LVGL screen is built.
     * This hides the ST7789's undefined RAM contents during power-up.
     */
    (void)lumi_panel_backlight_init();
    (void)lumi_panel_set_backlight(false);

    /* Keep the known-good ST7789 baseline timing. This panel has no TE
     * feedback wired, so forcing FRCTRL2/porch cannot synchronize RAMWR.
     */
    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
        lumi_diag_report('E', "Panel init: SPI/DC not ready");
        return -ENODEV;
    }

    uint8_t command = 0x21; /* INVON */
    struct spi_buf buffer = {.buf = &command, .len = sizeof(command)};
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1};

    int err = gpio_pin_set_dt(&dc, 1);
    if (err == 0) {
        err = spi_write_dt(&bus, &buffers);
    }
    if (err) {
        LOG_ERR("Panel inversion setup failed: %d", err);
        lumi_diag_report('E', "Panel INVON failed rc=%d", err);
    } else {
        lumi_diag_report('I', "Panel init OK");
    }
    return err;
}

int lumi_panel_set_sleep(bool sleeping) {
    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
        lumi_diag_report('E', "Panel %s: SPI/DC not ready",
                         sleeping ? "off" : "on");
        return -ENODEV;
    }

    /* Soft sleep deliberately uses DISPOFF instead of SLPIN. Keeping the
     * controller awake makes wake reliable and avoids the 120 ms SLPOUT
     * recovery window that previously lost the first LVGL/Now Playing redraw.
     */
    uint8_t command = sleeping ? 0x28 : 0x29; /* DISPOFF / DISPON */
    struct spi_buf buffer = {.buf = &command, .len = sizeof(command)};
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1};

    int err = gpio_pin_set_dt(&dc, 1);
    if (err == 0) {
        err = spi_write_dt(&bus, &buffers);
    }
    if (err) {
        LOG_ERR("Panel %s failed: %d", sleeping ? "off" : "on", err);
        lumi_diag_report('E', "Panel %s failed rc=%d",
                         sleeping ? "off" : "on", err);
        return err;
    }

    k_msleep(sleeping ? 5 : 10);

    if (sleeping) {
        (void)lumi_panel_set_backlight(false);
    } else {
        /* Keep the backlight off until LVGL has invalidated/redrawn the UI. */
        (void)lumi_panel_set_backlight(false);
    }

    lumi_diag_report('I', "Panel display %s", sleeping ? "OFF" : "ON");
    return 0;
}


int lumi_panel_enter_deep_sleep(void) {
    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
        lumi_diag_report('E', "Panel deep sleep: SPI/DC not ready");
        return -ENODEV;
    }

    /* Normal soft sleep intentionally keeps the ST7789 awake for a fast
     * DISPON wake. True deep sleep does not need that tradeoff because wake
     * is a cold boot, so fully sleep the controller before VCC is removed.
     */
    (void)lumi_panel_set_backlight(false);

    uint8_t command = 0x28; /* DISPOFF */
    struct spi_buf buffer = {.buf = &command, .len = sizeof(command)};
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1};

    int err = gpio_pin_set_dt(&dc, 1);
    if (err == 0) {
        err = spi_write_dt(&bus, &buffers);
    }
    if (err) {
        lumi_diag_report('E', "Panel deep DISPOFF rc=%d", err);
        return err;
    }

    k_msleep(5);

    command = 0x10; /* SLPIN */
    err = gpio_pin_set_dt(&dc, 1);
    if (err == 0) {
        err = spi_write_dt(&bus, &buffers);
    }
    if (err) {
        lumi_diag_report('E', "Panel SLPIN rc=%d", err);
        return err;
    }

    /* ST7789 requires the sleep-in transition to settle before power removal. */
    k_msleep(120);
    lumi_diag_report('I', "Panel entered deep sleep");
    return 0;
}
