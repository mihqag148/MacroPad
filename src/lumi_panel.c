/* SPDX-License-Identifier: MIT */
#include <errno.h>
#include <zephyr/device.h>
#include <zephyr/drivers/gpio.h>
#include <zephyr/drivers/display.h>
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

static int lumi_panel_command_with_data(uint8_t command,
                                        const uint8_t *data,
                                        size_t len) {
    int err = lumi_panel_command(command);
    if (err != 0 || len == 0U) {
        return err;
    }

    err = gpio_pin_set_dt(&dc, 0); /* data */
    if (err != 0) {
        return err;
    }

    struct spi_buf buffer = {
        .buf = (void *)data,
        .len = len,
    };
    const struct spi_buf_set buffers = {
        .buffers = &buffer,
        .count = 1,
    };

    return spi_write_dt(&bus, &buffers);
}

static int lumi_panel_write_data(const uint8_t *data, size_t len) {
    if (!data || len == 0U) {
        return -EINVAL;
    }

    int err = gpio_pin_set_dt(&dc, 0); /* data */
    if (err != 0) {
        return err;
    }

    struct spi_buf buffer = {
        .buf = (void *)data,
        .len = len,
    };
    const struct spi_buf_set buffers = {
        .buffers = &buffer,
        .count = 1,
    };

    return spi_write_dt(&bus, &buffers);
}

int lumi_panel_init(void) {
    /* This ST7789 panel variant needs display inversion enabled for
     * normal black/white polarity. D/C is active-low: logical 1 means
     * command (physical 0). BL is tied to 3V3.
     */
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


int lumi_panel_render_rgb332_scaled(const uint8_t *src,
                                    uint16_t src_w,
                                    uint16_t src_h) {
    if (!src || src_w == 0U || src_h == 0U) {
        return -EINVAL;
    }

    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
        return -ENODEV;
    }

    enum {
        OUT_W = 320,
        OUT_H = 172,
        STRIPE_H = 16,
        PANEL_Y_OFFSET = 34
    };

    static uint8_t stripe[OUT_W * STRIPE_H * 2];

    /* Program one full-screen GRAM window for the entire frame. Subsequent
     * data writes continue the ST7789 RAM pointer instead of issuing a new
     * CASET/RASET/RAMWR sequence for every stripe.
     */
    const uint16_t x0 = 0U;
    const uint16_t x1 = OUT_W - 1U;
    const uint16_t y0 = PANEL_Y_OFFSET;
    const uint16_t y1 = PANEL_Y_OFFSET + OUT_H - 1U;

    uint8_t col[4] = {
        (uint8_t)(x0 >> 8), (uint8_t)x0,
        (uint8_t)(x1 >> 8), (uint8_t)x1,
    };
    uint8_t row[4] = {
        (uint8_t)(y0 >> 8), (uint8_t)y0,
        (uint8_t)(y1 >> 8), (uint8_t)y1,
    };

    int err = lumi_panel_command_with_data(0x2A, col, sizeof(col)); /* CASET */
    if (err != 0) {
        return err;
    }

    err = lumi_panel_command_with_data(0x2B, row, sizeof(row)); /* RASET */
    if (err != 0) {
        return err;
    }

    err = lumi_panel_command(0x2C); /* RAMWR */
    if (err != 0) {
        return err;
    }

    bool exact_size = src_w == OUT_W && src_h == OUT_H;
    bool exact_2x = src_w == 160U && src_h == 86U;

    for (uint16_t base_y = 0; base_y < OUT_H; base_y += STRIPE_H) {
        uint16_t rows = OUT_H - base_y;
        if (rows > STRIPE_H) {
            rows = STRIPE_H;
        }

        size_t out = 0U;

        for (uint16_t oy = 0; oy < rows; oy++) {
            uint16_t y = base_y + oy;
            uint16_t sy = exact_size
                ? y
                : (exact_2x
                    ? (uint16_t)(y >> 1)
                    : (uint16_t)(((uint32_t)y * src_h) / OUT_H));

            if (sy >= src_h) {
                sy = src_h - 1U;
            }

            for (uint16_t x = 0; x < OUT_W; x++) {
                uint16_t sx = exact_size
                    ? x
                    : (exact_2x
                        ? (uint16_t)(x >> 1)
                        : (uint16_t)(((uint32_t)x * src_w) / OUT_W));

                if (sx >= src_w) {
                    sx = src_w - 1U;
                }

                uint8_t v = src[(size_t)sy * src_w + sx];
                uint16_t r5 = (uint16_t)((v >> 5) & 0x07U) * 31U / 7U;
                uint16_t g6 = (uint16_t)((v >> 2) & 0x07U) * 63U / 7U;
                uint16_t b5 = (uint16_t)(v & 0x03U) * 31U / 3U;
                uint16_t rgb565 =
                    (uint16_t)((r5 << 11) | (g6 << 5) | b5);

                stripe[out++] = (uint8_t)(rgb565 >> 8);
                stripe[out++] = (uint8_t)(rgb565 & 0xFFU);
            }
        }

        err = lumi_panel_write_data(stripe, out);
        if (err != 0) {
            return err;
        }
    }

    return 0;
}

