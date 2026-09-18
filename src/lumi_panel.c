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

static int lumi_panel_command(uint8_t command) {
    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
        return -ENODEV;
    }

    struct spi_buf buffer = {.buf = &command, .len = sizeof(command)};
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1};

    int err = gpio_pin_set_dt(&dc, 1);
    if (err == 0) {
        err = spi_write_dt(&bus, &buffers);
    }

    return err;
}

int lumi_panel_init(void) {
    int err = lumi_panel_command(0x21); /* INVON */
    if (err) {
        LOG_ERR("Panel inversion setup failed: %d", err);
    }
    return err;
}

int lumi_panel_set_sleep(bool sleeping) {
    int err;

    if (sleeping) {
        err = lumi_panel_command(0x28); /* DISPOFF */
        if (err != 0) {
            return err;
        }

        k_msleep(10);
        return lumi_panel_command(0x10); /* SLPIN */
    }

    err = lumi_panel_command(0x11); /* SLPOUT */
    if (err != 0) {
        return err;
    }

    k_msleep(120);
    return lumi_panel_command(0x29); /* DISPON */
}
