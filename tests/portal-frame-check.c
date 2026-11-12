// Offline pixel/coordinate contract: no desktop, portal, PipeWire daemon or user data.
#include "../src/AxmolHub/Platforms/Linux/portal-frame.h"
#include <assert.h>
#include <stdio.h>

int main(void)
{
    // Six distinct pixels inside a larger, padded buffer, with a nonzero chunk offset.
    uint8_t src[160] = {0}, dst[24];
    struct hub_frame_geometry g = { .buffer_width = 5, .buffer_height = 4,
        .x = 1, .y = 1, .width = 3, .height = 2, .has_crop = 1 };
    for (uint32_t y = 0; y < 2; ++y)
        for (uint32_t x = 0; x < 3; ++x) {
            uint8_t *p = src + 8 + (y + 1) * 24 + (x + 1) * 4;
            p[0] = y * 3 + x + 1; p[2] = p[0] + 10;
        }
    const uint8_t expected[8][6] = {
        {1,2,3,4,5,6}, {4,1,5,2,6,3}, {6,5,4,3,2,1}, {3,6,2,5,1,4},
        {3,2,1,6,5,4}, {1,4,2,5,3,6}, {4,5,6,1,2,3}, {6,3,5,2,4,1},
    };
    for (g.transform = 0; g.transform < 8; ++g.transform) {
        assert(hub_frame_copy(&g, src, sizeof(src), 8, 152, 24, false, dst));
        for (uint32_t i = 0; i < 6; ++i) {
            assert(dst[i*4] == expected[g.transform][i] && dst[i*4+3] == 255);
            uint32_t bx, by;
            double lx, ly;
            hub_frame_buffer_point(&g, i % hub_frame_width(&g), i / hub_frame_width(&g), &bx, &by);
            assert(src[8 + by*24 + bx*4] == dst[i*4]);
            assert(hub_frame_logical_point(&g, 10, 8, i % hub_frame_width(&g), i / hub_frame_width(&g), &lx, &ly));
            assert(lx == bx * 2.0 && ly == by * 2.0);
        }
        assert(hub_frame_copy(&g, src, sizeof(src), 8, 152, 24, true, dst));
        assert(dst[0] == expected[g.transform][0] + 10);
    }
    g.transform = 0;
    // Reproduce the real 4K/150% fixed canvas without allocating a display-sized image.
    g.buffer_width = 5120; g.buffer_height = 2880; g.x = 16; g.y = 8; g.width = 2500; g.height = 1700;
    double lx, ly;
    assert(hub_frame_logical_point(&g, 2560, 1440, 100, 200, &lx, &ly));
    assert(lx == 58 && ly == 104); // Add crop offset before conversion, not after scaling the cropped width.
    assert(!hub_frame_logical_point(&g, 2560, 1440, 2500, 0, &lx, &ly));
    g.x = 5000; assert(!hub_frame_valid(&g));
    g.x = 0; g.width = 0; assert(!hub_frame_valid(&g));
    g.width = 100; g.transform = 8; assert(!hub_frame_valid(&g));
    g = (struct hub_frame_geometry){ .buffer_width=5, .buffer_height=4, .x=1, .y=1, .width=3, .height=2 };
    assert(!hub_frame_copy(&g, src, 50, 8, 152, 24, false, dst));
    assert(!hub_frame_copy(&g, src, sizeof(src), 8, 20, 24, false, dst));
    assert(!hub_frame_copy(&g, src, sizeof(src), 8, 152, -24, false, dst));
    puts("PASS: cropped padded/offset pixels, all eight SPA transforms, inverse pointer mapping, 4K/150% geometry and invalid bounds.");
    return 0;
}
