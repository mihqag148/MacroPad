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

    const struct device *display = DEVICE_DT_GET(PANEL);
    if (!device_is_ready(display)) {
        return -ENODEV;
    }

    /* Render in 8-row RGB565 stripes to avoid a 110 KB framebuffer.
     * This bypasses LVGL for custom animation frames and sends the panel
     * a continuous full-screen image at the highest practical SPI rate.
     */
    enum { OUT_W = 320, OUT_H = 172, STRIPE_H = 8 };
    static uint8_t stripe[OUT_W * STRIPE_H * 2];

    bool exact_2x = src_w == 160U && src_h == 86U;

    for (uint16_t y0 = 0; y0 < OUT_H; y0 += STRIPE_H) {
        uint16_t rows = OUT_H - y0;
        if (rows > STRIPE_H) {
            rows = STRIPE_H;
        }

        size_t out = 0U;

        for (uint16_t oy = 0; oy < rows; oy++) {
            uint16_t y = y0 + oy;
            uint16_t sy = exact_2x
                ? (uint16_t)(y >> 1)
                : (uint16_t)(((uint32_t)y * src_h) / OUT_H);
            if (sy >= src_h) {
                sy = src_h - 1U;
            }

            for (uint16_t x = 0; x < OUT_W; x++) {
                uint16_t sx = exact_2x
                    ? (uint16_t)(x >> 1)
                    : (uint16_t)(((uint32_t)x * src_w) / OUT_W);
                if (sx >= src_w) {
                    sx = src_w - 1U;
                }

                uint8_t v = src[(size_t)sy * src_w + sx];
                uint16_t r5 = (uint16_t)((v >> 5) & 0x07U) * 31U / 7U;
                uint16_t g6 = (uint16_t)((v >> 2) & 0x07U) * 63U / 7U;
                uint16_t b5 = (uint16_t)(v & 0x03U) * 31U / 3U;
                uint16_t rgb565 =
                    (uint16_t)((r5 << 11) | (g6 << 5) | b5);

                /* ST7789 expects MSB first on the wire. */
                stripe[out++] = (uint8_t)(rgb565 >> 8);
                stripe[out++] = (uint8_t)(rgb565 & 0xFFU);
            }
        }

        struct display_buffer_descriptor desc = {
            .buf_size = out,
            .width = OUT_W,
            .height = rows,
            .pitch = OUT_W,
        };

        int err = display_write(display, 0, y0, &desc, stripe);
        if (err) {
            return err;
        }
    }

    return 0;
}
