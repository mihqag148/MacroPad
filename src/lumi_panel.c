/* SPDX-License-Identifier: MIT */
#include <errno.h>
#include <string.h>
#include <zephyr/device.h>
#include <zephyr/drivers/gpio.h>
#include <zephyr/drivers/spi.h>
#include <zephyr/kernel.h>
#include <zephyr/logging/log.h>
#include <zephyr/sys/atomic.h>
#include <lvgl.h>
#include "lumi_panel.h"

LOG_MODULE_REGISTER(lumi_panel, CONFIG_ZMK_LOG_LEVEL);

#define PANEL DT_CHOSEN(zephyr_display)
#define LUMI_LOGICAL_WIDTH 320U
#define LUMI_LOGICAL_HEIGHT 172U
#define LUMI_NATIVE_WIDTH 172U
#define LUMI_NATIVE_HEIGHT 320U
#define LUMI_NATIVE_X_OFFSET 34U
#define LUMI_ROTATE_BITMAP_BYTES                                                \
    ((LUMI_LOGICAL_WIDTH * LUMI_LOGICAL_HEIGHT + 7U) / 8U)

BUILD_ASSERT(sizeof(lv_color_t) == 2U,
             "Lumi panel flush requires LVGL RGB565 pixels");

static const struct spi_dt_spec bus =
    SPI_DT_SPEC_GET(PANEL, SPI_OP_MODE_MASTER | SPI_WORD_SET(8), 0);
static const struct gpio_dt_spec dc = GPIO_DT_SPEC_GET(PANEL, cmd_data_gpios);

/* spi_transceive_cb() retains these descriptors until its callback runs. */
static struct spi_buf async_buffer;
static const struct spi_buf_set async_buffers = {
    .buffers = &async_buffer,
    .count = 1U,
};
static atomic_t async_flush_busy;

/* The LVGL partial draw buffer is rotated in place. A bitmap avoids a second
 * RGB565 buffer while still supporting every possible partial-area shape.
 */
static uint8_t rotate_visited[LUMI_ROTATE_BITMAP_BYTES];

static int lumi_panel_send_command(uint8_t command) {
    struct spi_buf buffer = {.buf = &command, .len = 1U};
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1U};

    int err = gpio_pin_set_dt(&dc, 1);
    if (err != 0) {
        return err;
    }

    return spi_write_dt(&bus, &buffers);
}

static int lumi_panel_send_data(const uint8_t *data, size_t len) {
    if (!data || len == 0U) {
        return -EINVAL;
    }

    struct spi_buf buffer = {.buf = (void *)data, .len = len};
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1U};

    int err = gpio_pin_set_dt(&dc, 0);
    if (err != 0) {
        return err;
    }

    return spi_write_dt(&bus, &buffers);
}

/* Match the old MADCTL MX|MV landscape orientation without using MV:
 * logical (x, y) -> native (y, 319 - x). The native panel has a 34-pixel
 * column offset. Keeping the controller in native scan order avoids the
 * cross-axis scan that made a landscape frame tear vertically.
 */
static int lumi_panel_set_window(const lv_area_t *area) {
    if (!area || area->x1 < 0 || area->y1 < 0 ||
        area->x2 >= (lv_coord_t)LUMI_LOGICAL_WIDTH ||
        area->y2 >= (lv_coord_t)LUMI_LOGICAL_HEIGHT ||
        area->x1 > area->x2 || area->y1 > area->y2) {
        return -EINVAL;
    }

    uint16_t col_start = (uint16_t)area->y1 + LUMI_NATIVE_X_OFFSET;
    uint16_t col_end = (uint16_t)area->y2 + LUMI_NATIVE_X_OFFSET;
    uint16_t row_start = (uint16_t)(LUMI_NATIVE_HEIGHT - 1U - area->x2);
    uint16_t row_end = (uint16_t)(LUMI_NATIVE_HEIGHT - 1U - area->x1);
    uint8_t data[4];

    int err = lumi_panel_send_command(0x2A); /* CASET */
    if (err != 0) {
        return err;
    }

    data[0] = (uint8_t)(col_start >> 8);
    data[1] = (uint8_t)col_start;
    data[2] = (uint8_t)(col_end >> 8);
    data[3] = (uint8_t)col_end;
    err = lumi_panel_send_data(data, sizeof(data));
    if (err != 0) {
        return err;
    }

    err = lumi_panel_send_command(0x2B); /* RASET */
    if (err != 0) {
        return err;
    }

    data[0] = (uint8_t)(row_start >> 8);
    data[1] = (uint8_t)row_start;
    data[2] = (uint8_t)(row_end >> 8);
    data[3] = (uint8_t)row_end;
    err = lumi_panel_send_data(data, sizeof(data));
    if (err != 0) {
        return err;
    }

    return lumi_panel_send_command(0x2C); /* RAMWR */
}

static size_t lumi_panel_rotated_index(size_t index,
                                       uint32_t width,
                                       uint32_t height) {
    size_t x = index % width;
    size_t y = index / width;

    /* Destination is native row-major: logical x descends by output row,
     * while logical y increases across each output row.
     */
    return (width - 1U - x) * height + y;
}

static bool lumi_panel_visited(size_t index) {
    return (rotate_visited[index >> 3] & BIT(index & 7U)) != 0U;
}

static void lumi_panel_mark_visited(size_t index) {
    rotate_visited[index >> 3] |= BIT(index & 7U);
}

static int lumi_panel_rotate_area(lv_color_t *pixels,
                                  uint32_t width,
                                  uint32_t height) {
    if (!pixels || width == 0U || height == 0U) {
        return -EINVAL;
    }

    size_t pixel_count = (size_t)width * height;
    if (pixel_count > LUMI_LOGICAL_WIDTH * LUMI_LOGICAL_HEIGHT) {
        return -E2BIG;
    }

    memset(rotate_visited, 0, (pixel_count + 7U) / 8U);

    for (size_t start = 0U; start < pixel_count; start++) {
        if (lumi_panel_visited(start)) {
            continue;
        }

        lv_color_t carried = pixels[start];
        size_t current = start;

        do {
            size_t next = lumi_panel_rotated_index(current, width, height);
            lv_color_t displaced = pixels[next];
            pixels[next] = carried;
            carried = displaced;
            lumi_panel_mark_visited(current);
            current = next;
        } while (current != start);
    }

    return 0;
}

static void lumi_panel_async_done(const struct device *dev,
                                  int result,
                                  void *userdata) {
    ARG_UNUSED(dev);

    lv_disp_drv_t *drv = (lv_disp_drv_t *)userdata;
    atomic_clear(&async_flush_busy);

    if (result != 0) {
        LOG_ERR("Async TFT payload failed: %d", result);
    }

    /* LVGL may reuse the rotated draw buffer only after EasyDMA finishes. */
    lv_disp_flush_ready(drv);
}

static void lumi_panel_async_flush(lv_disp_drv_t *drv,
                                   const lv_area_t *area,
                                   lv_color_t *color_p) {
    if (!drv || !area || !color_p) {
        if (drv) {
            lv_disp_flush_ready(drv);
        }
        return;
    }

    if (!atomic_cas(&async_flush_busy, 0, 1)) {
        LOG_ERR("Unexpected overlapping TFT flush");
        lv_disp_flush_ready(drv);
        return;
    }

    uint32_t width = (uint32_t)(area->x2 - area->x1 + 1);
    uint32_t height = (uint32_t)(area->y2 - area->y1 + 1);
    int err = lumi_panel_rotate_area(color_p, width, height);
    if (err != 0) {
        LOG_ERR("TFT software rotation failed: %d", err);
        atomic_clear(&async_flush_busy);
        lv_disp_flush_ready(drv);
        return;
    }

    err = lumi_panel_set_window(area);
    if (err != 0) {
        LOG_ERR("TFT window setup failed: %d", err);
        atomic_clear(&async_flush_busy);
        lv_disp_flush_ready(drv);
        return;
    }

    err = gpio_pin_set_dt(&dc, 0);
    if (err != 0) {
        LOG_ERR("TFT D/C setup failed: %d", err);
        atomic_clear(&async_flush_busy);
        lv_disp_flush_ready(drv);
        return;
    }

    async_buffer.buf = color_p;
    async_buffer.len = (size_t)width * height * sizeof(lv_color_t);
    err = spi_transceive_cb(bus.bus,
                            &bus.config,
                            &async_buffers,
                            NULL,
                            lumi_panel_async_done,
                            drv);
    if (err != 0) {
        atomic_clear(&async_flush_busy);
        LOG_ERR("Async TFT transfer start failed: %d", err);
        lv_disp_flush_ready(drv);
    }
}

int lumi_panel_enable_async_flush(void) {
    lv_disp_t *disp = lv_disp_get_default();
    if (!disp || !disp->driver) {
        return -ENODEV;
    }

    /* Zephyr initializes the display from its native 172x320 geometry. The
     * UI remains logical landscape; this flush callback performs the 90-degree
     * software rotation before addressing native GRAM.
     */
    disp->driver->hor_res = LUMI_LOGICAL_WIDTH;
    disp->driver->ver_res = LUMI_LOGICAL_HEIGHT;
    disp->driver->flush_cb = lumi_panel_async_flush;
    LOG_INF("LVGL TFT flush: 320x172 software rotation via async SPI");
    return 0;
}

int lumi_panel_init(void) {
    /* Keep the ST7789 driver's normal PORCTRL/FRCTRL2 baseline (~60 Hz).
     * This panel needs inversion enabled for normal black/white polarity.
     */
    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
        return -ENODEV;
    }

    int err = lumi_panel_send_command(0x21); /* INVON */
    if (err != 0) {
        LOG_ERR("Panel inversion setup failed: %d", err);
    }
    return err;
}

int lumi_panel_set_sleep(bool sleeping) {
    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
        return -ENODEV;
    }

    int err = lumi_panel_send_command(sleeping ? 0x28 : 0x11);
    if (err != 0) {
        return err;
    }

    if (sleeping) {
        k_msleep(10);
        return lumi_panel_send_command(0x10); /* SLPIN */
    }

    k_msleep(120);
    return lumi_panel_send_command(0x29); /* DISPON */
}
