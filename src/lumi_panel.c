/* SPDX-License-Identifier: MIT */
#include <errno.h>
#include <zephyr/device.h>
#include <zephyr/drivers/gpio.h>
#include <zephyr/drivers/spi.h>
#include <zephyr/logging/log.h>
#include <zephyr/kernel.h>
#include <lvgl.h>
#include "lumi_panel.h"

LOG_MODULE_REGISTER(lumi_panel, CONFIG_ZMK_LOG_LEVEL);
#define PANEL DT_CHOSEN(zephyr_display)
static const struct spi_dt_spec bus =
    SPI_DT_SPEC_GET(PANEL, SPI_OP_MODE_MASTER | SPI_WORD_SET(8), 0);
static const struct gpio_dt_spec dc = GPIO_DT_SPEC_GET(PANEL, cmd_data_gpios);

/* Held across the entire command/window/DMA transaction, including sleep.
 * A semaphore can be released by the SPI completion ISR (unlike a mutex).
 */
K_SEM_DEFINE(panel_idle, 1, 1);
#define LOGICAL_WIDTH 320
#define LOGICAL_HEIGHT 172
#define ROTATED_PIXELS ((LOGICAL_WIDTH * LOGICAL_HEIGHT * CONFIG_LV_Z_VDB_SIZE) / 100)
BUILD_ASSERT(sizeof(lv_color_t) == 2, "Panel requires RGB565");
static lv_color_t rotated[ROTATED_PIXELS] __aligned(4);
/* Descriptors and pixel storage must outlive the asynchronous call. */
static struct spi_buf pixel_buffer;
static const struct spi_buf_set pixel_buffers = {.buffers = &pixel_buffer, .count = 1};

static int lumi_panel_send_command(uint8_t command) {
    struct spi_buf buffer = {.buf = &command, .len = 1U};
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1};

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
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1};

    int err = gpio_pin_set_dt(&dc, 0);
    if (err != 0) {
        return err;
    }

    return spi_write_dt(&bus, &buffers);
}

static int lumi_panel_set_window(const lv_area_t *area) {
    if (!area) {
        return -EINVAL;
    }

    uint16_t x1 = (uint16_t)(LOGICAL_HEIGHT - 1 - area->y2 + DT_PROP(PANEL, x_offset));
    uint16_t x2 = (uint16_t)(LOGICAL_HEIGHT - 1 - area->y1 + DT_PROP(PANEL, x_offset));
    uint16_t y1 = (uint16_t)(area->x1 + DT_PROP(PANEL, y_offset));
    uint16_t y2 = (uint16_t)(area->x2 + DT_PROP(PANEL, y_offset));

    uint8_t data[4];

    int err = lumi_panel_send_command(0x2A); /* CASET */
    if (err != 0) {
        return err;
    }

    data[0] = (uint8_t)(x1 >> 8);
    data[1] = (uint8_t)x1;
    data[2] = (uint8_t)(x2 >> 8);
    data[3] = (uint8_t)x2;
    err = lumi_panel_send_data(data, sizeof(data));
    if (err != 0) {
        return err;
    }

    err = lumi_panel_send_command(0x2B); /* RASET */
    if (err != 0) {
        return err;
    }

    data[0] = (uint8_t)(y1 >> 8);
    data[1] = (uint8_t)y1;
    data[2] = (uint8_t)(y2 >> 8);
    data[3] = (uint8_t)y2;
    err = lumi_panel_send_data(data, sizeof(data));
    if (err != 0) {
        return err;
    }

    return lumi_panel_send_command(0x2C); /* RAMWR */
}

static void lumi_panel_async_done(const struct device *dev,
                                  int result,
                                  void *userdata) {
    ARG_UNUSED(dev);

    lv_disp_drv_t *drv = (lv_disp_drv_t *)userdata;
    k_sem_give(&panel_idle);

    if (result != 0) {
        LOG_ERR("Async TFT payload failed: %d", result);
    }

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

    uint32_t width = (uint32_t)(area->x2 - area->x1 + 1);
    uint32_t height = (uint32_t)(area->y2 - area->y1 + 1);
    if (area->x1 < 0 || area->y1 < 0 || area->x2 >= LOGICAL_WIDTH ||
        area->y2 >= LOGICAL_HEIGHT || area->x2 < area->x1 || area->y2 < area->y1 ||
        width * height > ARRAY_SIZE(rotated)) {
        LOG_ERR("Invalid TFT flush area");
        lv_disp_flush_ready(drv);
        return;
    }

    k_sem_take(&panel_idle, K_FOREVER);
    /* Old MX|MV landscape -> native MADCTL=0:
     * (x,y) -> (171-y,x). Emit native rows, columns increasing.
     * Copy lv_color_t unchanged: LV_COLOR_16_SWAP already sets wire byte order.
     */
    for (uint32_t x = 0; x < width; x++) {
        for (uint32_t y = 0; y < height; y++) {
            rotated[x * height + y] = color_p[(height - 1 - y) * width + x];
        }
    }

    int err = lumi_panel_set_window(area);
    if (err != 0) {
        k_sem_give(&panel_idle);
        LOG_ERR("TFT window setup failed: %d", err);
        lv_disp_flush_ready(drv);
        return;
    }

    pixel_buffer.buf = rotated;
    pixel_buffer.len = (size_t)width * height * sizeof(lv_color_t);

    err = gpio_pin_set_dt(&dc, 0);
    if (err != 0) {
        k_sem_give(&panel_idle);
        LOG_ERR("TFT D/C setup failed: %d", err);
        lv_disp_flush_ready(drv);
        return;
    }

    err = spi_transceive_cb(bus.bus,
                            &bus.config,
                            &pixel_buffers,
                            NULL,
                            lumi_panel_async_done,
                            drv);
    if (err != 0) {
        k_sem_give(&panel_idle);
        LOG_ERR("Async TFT transfer start failed: %d", err);
        lv_disp_flush_ready(drv);
    }
}

int lumi_panel_enable_async_flush(void) {
    lv_disp_t *disp = lv_disp_get_default();
    if (!disp || !disp->driver) {
        return -ENODEV;
    }

    if (disp->driver->draw_buf->size > ARRAY_SIZE(rotated)) {
        return -ENOMEM;
    }
    disp->driver->hor_res = LOGICAL_WIDTH;
    disp->driver->ver_res = LOGICAL_HEIGHT;
    disp->driver->rotated = LV_DISP_ROT_NONE;
    disp->driver->sw_rotate = 0;
    disp->driver->flush_cb = lumi_panel_async_flush;
    lv_disp_drv_update(disp, disp->driver);
    LOG_INF("LVGL TFT flush switched to asynchronous SPI");
    return 0;
}

int lumi_panel_init(void) {
    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
        return -ENODEV;
    }
    /* Zephyr initializes native MADCTL, baseline porch and default C6=0x0f.
     * Keep panel inversion required by this module; do not override timing.
     */
    k_sem_take(&panel_idle, K_FOREVER);
    int err = lumi_panel_send_command(0x21); /* INVON */
    k_sem_give(&panel_idle);
    return err;
}

int lumi_panel_set_sleep(bool sleeping) {
    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
        return -ENODEV;
    }

    k_sem_take(&panel_idle, K_FOREVER);
    int err = lumi_panel_send_command(sleeping ? 0x28 : 0x11);
    if (err == 0) {
        k_msleep(sleeping ? 10 : 120);
        err = lumi_panel_send_command(sleeping ? 0x10 : 0x29);
    }
    k_sem_give(&panel_idle);
    return err;
}
