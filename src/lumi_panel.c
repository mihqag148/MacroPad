/* SPDX-License-Identifier: MIT */
#include <errno.h>
#include <zephyr/device.h>
#include <zephyr/drivers/gpio.h>
#include <zephyr/drivers/spi.h>
#include <zephyr/logging/log.h>
#include <zephyr/kernel.h>
#include "lumi_panel.h"

LOG_MODULE_REGISTER(lumi_panel, CONFIG_ZMK_LOG_LEVEL);
#define PANEL DT_CHOSEN(zephyr_display)
static const struct spi_dt_spec bus =
    SPI_DT_SPEC_GET(PANEL, SPI_OP_MODE_MASTER | SPI_WORD_SET(8), 0);
static const struct gpio_dt_spec dc = GPIO_DT_SPEC_GET(PANEL, cmd_data_gpios);

int lumi_panel_init(void) {
    /* Keep the known-good ST7789 baseline timing. This panel has no TE
     * feedback wired, so forcing FRCTRL2/porch cannot synchronize RAMWR.
     */
    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
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
    }
    return err;
}

int lumi_panel_set_sleep(bool sleeping) {
    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
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
        return err;
    }

    k_msleep(sleeping ? 5 : 10);
    return 0;
}
