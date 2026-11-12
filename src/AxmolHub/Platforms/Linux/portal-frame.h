// Pixel geometry for a portal stream. All coordinates here are buffer pixels, never desktop DPI values.
#ifndef HUB_PORTAL_FRAME_H
#define HUB_PORTAL_FRAME_H

#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>

#define HUB_MAX_FRAME (64u * 1024u * 1024u)

struct hub_frame_geometry {
    uint32_t buffer_width, buffer_height;
    uint32_t x, y, width, height;
    uint32_t transform, has_crop, format;
};

static inline bool hub_frame_valid(const struct hub_frame_geometry *g)
{
    return g->buffer_width && g->buffer_height && g->width && g->height && g->transform <= 7
        && g->x < g->buffer_width && g->y < g->buffer_height
        && g->width <= g->buffer_width - g->x && g->height <= g->buffer_height - g->y
        && (uint64_t)g->width * g->height * 4 <= HUB_MAX_FRAME;
}

static inline uint32_t hub_frame_width(const struct hub_frame_geometry *g)
{
    return (g->transform & 1) ? g->height : g->width;
}
static inline uint32_t hub_frame_height(const struct hub_frame_geometry *g)
{
    return (g->transform & 1) ? g->width : g->height;
}

// SPA metadata describes the transform already applied to the buffer, not the operation to apply next.
// Undo it for presentation (e.g. metadata 90 means render clockwise). Both extraction and input use
// the same inverse sampling address; flipped cases undo rotation then reflect the displayed image.
static inline void hub_frame_buffer_point(const struct hub_frame_geometry *g, uint32_t x, uint32_t y,
                                         uint32_t *bx, uint32_t *by)
{
    uint32_t px, py;
    switch (g->transform) {
    case 1: px = y; py = g->height - 1 - x; break;
    case 2: px = g->width - 1 - x; py = g->height - 1 - y; break;
    case 3: px = g->width - 1 - y; py = x; break;
    case 4: px = g->width - 1 - x; py = y; break;
    case 5: px = y; py = x; break;
    case 6: px = x; py = g->height - 1 - y; break;
    case 7: px = g->width - 1 - y; py = g->height - 1 - x; break;
    default: px = x; py = y; break;
    }
    *bx = g->x + px;
    *by = g->y + py;
}

static inline bool hub_frame_logical_point(const struct hub_frame_geometry *g, uint32_t lw, uint32_t lh,
                                           uint32_t x, uint32_t y, double *lx, double *ly)
{
    if (!hub_frame_valid(g) || !lw || !lh || x >= hub_frame_width(g) || y >= hub_frame_height(g)) return false;
    uint32_t bx, by;
    hub_frame_buffer_point(g, x, y, &bx, &by);
    *lx = (double)bx * lw / g->buffer_width;
    *ly = (double)by * lh / g->buffer_height;
    return true;
}

// Accept only bounded, positively-strided CPU-mapped BGRA/RGBA-family frames. No black-pixel heuristics.
static inline bool hub_frame_copy(const struct hub_frame_geometry *g, const uint8_t *src,
                                 uint32_t maxsize, uint32_t offset, uint32_t chunk_size, int32_t stride,
                                 bool swap_red_blue, uint8_t *dst)
{
    if (!hub_frame_valid(g) || !src || !dst || stride < (int64_t)g->buffer_width * 4 || offset > maxsize) return false;
    uint64_t end = (uint64_t)stride * (g->y + g->height - 1) + (uint64_t)(g->x + g->width) * 4;
    if (end > maxsize - offset || end > chunk_size) return false;
    uint32_t w = hub_frame_width(g), h = hub_frame_height(g);
    for (uint32_t y = 0; y < h; ++y) {
        if (g->transform == 0 && !swap_red_blue) {
            memcpy(dst + (uint64_t)y * w * 4, src + offset + (uint64_t)(g->y + y) * stride + g->x * 4, w * 4);
            for (uint32_t x = 0; x < w; ++x) dst[((uint64_t)y * w + x) * 4 + 3] = 255;
            continue;
        }
        for (uint32_t x = 0; x < w; ++x) {
            uint32_t bx, by;
            hub_frame_buffer_point(g, x, y, &bx, &by);
            const uint8_t *p = src + offset + (uint64_t)by * stride + (uint64_t)bx * 4;
            uint8_t *q = dst + ((uint64_t)y * w + x) * 4;
            q[0] = p[swap_red_blue ? 2 : 0]; q[1] = p[1]; q[2] = p[swap_red_blue ? 0 : 2]; q[3] = 255;
        }
    }
    return true;
}
#endif
