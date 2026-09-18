/* SPDX-License-Identifier: MIT */
#include <errno.h>
#include <zephyr/device.h>
#include <zephyr/drivers/gpio.h>
#include <zephyr/drivers/spi.h>
#include <zephyr/logging/log.h>
#include "lumi_panel.h"

LOG_MODULE_REGISTER(lumi_panel, CONFIG_ZMK_LOG_LEVEL);
#define PANEL DT_CHOSEN(zephyr_display)
static const struct spi_dt_spec bus =
    SPI_DT_SPEC_GET(PANEL, SPI_OP_MODE_MASTER | SPI_WORD_SET(8), 0);
static const struct gpio_dt_spec dc = GPIO_DT_SPEC_GET(PANEL, cmd_data_gpios);

int lumi_panel_init(void) {
    /* Match the original CircuitPython setup: invert=False.
     * Send INVOFF once before the first LVGL frame.
     * D/C is active-low: logical 1 means command (physical 0).
     * BL is tied to 3V3; there is no backlight control.
     */
    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
        return -ENODEV;
    }
    uint8_t command = 0x20; /* INVOFF */
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
