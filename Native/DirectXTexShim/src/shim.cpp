// A plain C interface over DirectXTex for the pack optimizer, called through P/Invoke. It replaces the
// C++/CLI DirectXTexNet, which cannot run from a single-file or trimmed .NET application. Every function
// returns an HRESULT and catches all C++ exceptions; images are opaque handles owning a
// DirectX::ScratchImage, so there is no shared state between calls and the functions can be used from any
// number of threads on different images.
#include <DirectXTex.h>
#include <objbase.h>
#include <new>
#include <cstdint>

#ifndef DXS_SOURCE_TAG
#define DXS_SOURCE_TAG "unknown"
#endif
#ifndef DXS_SOURCE_COMMIT
#define DXS_SOURCE_COMMIT "unknown"
#endif

#define DXS_API extern "C" __declspec(dllexport)

using namespace DirectX;

struct DxsImage
{
    size_t width;
    size_t height;
    int format;
    size_t rowPitch;
    size_t slicePitch;
    uint8_t* pixels;
};

struct DxsMetadata
{
    size_t width;
    size_t height;
    size_t depth;
    size_t arraySize;
    size_t mipLevels;
    uint32_t miscFlags;
    uint32_t miscFlags2;
    int format;
    int dimension;
};

namespace
{
    template <typename F>
    HRESULT Guard(F&& body) noexcept
    {
        try
        {
            return body();
        }
        catch (const std::bad_alloc&)
        {
            return E_OUTOFMEMORY;
        }
        catch (...)
        {
            return E_FAIL;
        }
    }

    ScratchImage* From(void* handle) { return static_cast<ScratchImage*>(handle); }

    // Runs a function that fills a new ScratchImage and hands out its handle only on success.
    template <typename F>
    HRESULT MakeImage(void** out, F&& fill) noexcept
    {
        if (out == nullptr)
            return E_POINTER;
        *out = nullptr;
        return Guard([&]() -> HRESULT {
            auto* image = new ScratchImage();
            const HRESULT hr = fill(*image);
            if (FAILED(hr))
            {
                delete image;
                return hr;
            }

            *out = image;
            return S_OK;
        });
    }

    void ToDxs(const Image& image, DxsImage* out)
    {
        out->width = image.width;
        out->height = image.height;
        out->format = static_cast<int>(image.format);
        out->rowPitch = image.rowPitch;
        out->slicePitch = image.slicePitch;
        out->pixels = image.pixels;
    }

    Image FromDxs(const DxsImage& image)
    {
        Image result = {};
        result.width = image.width;
        result.height = image.height;
        result.format = static_cast<DXGI_FORMAT>(image.format);
        result.rowPitch = image.rowPitch;
        result.slicePitch = image.slicePitch;
        result.pixels = image.pixels;
        return result;
    }

    // WIC needs COM on the calling thread; join the multithreaded apartment if the thread has none.
    struct ComScope
    {
        HRESULT hr;
        ComScope() : hr(CoInitializeEx(nullptr, COINIT_MULTITHREADED)) {}
        ~ComScope()
        {
            if (SUCCEEDED(hr))
                CoUninitialize();
        }
    };
}

DXS_API int dxs_version() { return DIRECTX_TEX_VERSION; }
DXS_API const char* dxs_source_tag() { return DXS_SOURCE_TAG; }
DXS_API const char* dxs_source_commit() { return DXS_SOURCE_COMMIT; }

DXS_API HRESULT dxs_scratch_initialize2d(int format, size_t width, size_t height, size_t arraySize, size_t mipLevels, uint32_t cpFlags, void** out)
{
    return MakeImage(out, [&](ScratchImage& image) {
        return image.Initialize2D(static_cast<DXGI_FORMAT>(format), width, height, arraySize, mipLevels, static_cast<CP_FLAGS>(cpFlags));
    });
}

DXS_API void dxs_scratch_free(void* handle) { delete From(handle); }

DXS_API HRESULT dxs_scratch_metadata(void* handle, DxsMetadata* out)
{
    if (handle == nullptr || out == nullptr)
        return E_POINTER;
    const TexMetadata& m = From(handle)->GetMetadata();
    out->width = m.width;
    out->height = m.height;
    out->depth = m.depth;
    out->arraySize = m.arraySize;
    out->mipLevels = m.mipLevels;
    out->miscFlags = m.miscFlags;
    out->miscFlags2 = m.miscFlags2;
    out->format = static_cast<int>(m.format);
    out->dimension = static_cast<int>(m.dimension);
    return S_OK;
}

DXS_API size_t dxs_scratch_image_count(void* handle) { return handle == nullptr ? 0 : From(handle)->GetImageCount(); }

// Mirrors DirectXTexNet: an index outside the image set is E_INVALIDARG (the caller raises IndexOutOfRange).
DXS_API HRESULT dxs_scratch_get_image(void* handle, size_t mip, size_t item, size_t slice, DxsImage* out)
{
    if (handle == nullptr || out == nullptr)
        return E_POINTER;
    const Image* image = From(handle)->GetImage(mip, item, slice);
    if (image == nullptr)
        return E_INVALIDARG;
    ToDxs(*image, out);
    return S_OK;
}

DXS_API HRESULT dxs_scratch_create_image_copy(void* handle, size_t imageIndex, int allow1D, uint32_t cpFlags, void** out)
{
    return MakeImage(out, [&](ScratchImage& image) -> HRESULT {
        auto* source = From(handle);
        if (imageIndex >= source->GetImageCount())
            return E_INVALIDARG;
        return image.InitializeFromImage(source->GetImages()[imageIndex], allow1D != 0, static_cast<CP_FLAGS>(cpFlags));
    });
}

DXS_API HRESULT dxs_generate_mipmaps(void* handle, uint32_t filter, size_t levels, void** out)
{
    return MakeImage(out, [&](ScratchImage& image) {
        auto* source = From(handle);
        return GenerateMipMaps(source->GetImages(), source->GetImageCount(), source->GetMetadata(), static_cast<TEX_FILTER_FLAGS>(filter), levels, image);
    });
}

DXS_API HRESULT dxs_resize(void* handle, size_t width, size_t height, uint32_t filter, void** out)
{
    return MakeImage(out, [&](ScratchImage& image) {
        auto* source = From(handle);
        return Resize(source->GetImages(), source->GetImageCount(), source->GetMetadata(), width, height, static_cast<TEX_FILTER_FLAGS>(filter), image);
    });
}

DXS_API HRESULT dxs_compress(void* handle, int format, uint32_t flags, float threshold, void** out)
{
    // TEX_COMPRESS_PARALLEL needs an OpenMP build (E_NOTIMPL otherwise). This build has none, to stay free of the
    // vcomp140 redistributable; the flag is dropped, which only makes the same encode run on one thread.
    flags &= ~static_cast<uint32_t>(TEX_COMPRESS_PARALLEL);
    return MakeImage(out, [&](ScratchImage& image) {
        auto* source = From(handle);
        return Compress(source->GetImages(), source->GetImageCount(), source->GetMetadata(), static_cast<DXGI_FORMAT>(format), static_cast<TEX_COMPRESS_FLAGS>(flags), threshold, image);
    });
}

DXS_API HRESULT dxs_decompress(void* handle, int format, void** out)
{
    return MakeImage(out, [&](ScratchImage& image) {
        auto* source = From(handle);
        return Decompress(source->GetImages(), source->GetImageCount(), source->GetMetadata(), static_cast<DXGI_FORMAT>(format), image);
    });
}

// The DDS bytes are owned by the returned blob handle; release it with dxs_blob_free after copying.
DXS_API HRESULT dxs_save_dds_memory(void* handle, uint32_t flags, const uint8_t** data, size_t* size, void** blob)
{
    if (handle == nullptr || data == nullptr || size == nullptr || blob == nullptr)
        return E_POINTER;
    *data = nullptr;
    *size = 0;
    *blob = nullptr;
    return Guard([&]() -> HRESULT {
        auto* source = From(handle);
        auto* result = new Blob();
        const HRESULT hr = SaveToDDSMemory(source->GetImages(), source->GetImageCount(), source->GetMetadata(), static_cast<DDS_FLAGS>(flags), *result);
        if (FAILED(hr))
        {
            delete result;
            return hr;
        }

        *data = static_cast<const uint8_t*>(result->GetBufferPointer());
        *size = result->GetBufferSize();
        *blob = result;
        return S_OK;
    });
}

DXS_API void dxs_blob_free(void* blob) { delete static_cast<Blob*>(blob); }

DXS_API HRESULT dxs_load_dds_memory(const void* data, size_t size, uint32_t flags, void** out)
{
    return MakeImage(out, [&](ScratchImage& image) {
        return LoadFromDDSMemory(data, size, static_cast<DDS_FLAGS>(flags), nullptr, image);
    });
}

DXS_API HRESULT dxs_load_wic_memory(const void* data, size_t size, uint32_t flags, void** out)
{
    return MakeImage(out, [&](ScratchImage& image) {
        ComScope com;
        return LoadFromWICMemory(data, size, static_cast<WIC_FLAGS>(flags), nullptr, image);
    });
}

DXS_API HRESULT dxs_load_wic_file(const wchar_t* path, uint32_t flags, void** out)
{
    return MakeImage(out, [&](ScratchImage& image) {
        ComScope com;
        return LoadFromWICFile(path, static_cast<WIC_FLAGS>(flags), nullptr, image);
    });
}

DXS_API HRESULT dxs_copy_rectangle(const DxsImage* source, size_t x, size_t y, size_t width, size_t height, const DxsImage* destination, uint32_t filter, size_t xOffset, size_t yOffset)
{
    if (source == nullptr || destination == nullptr)
        return E_POINTER;
    return Guard([&]() -> HRESULT {
        const Image src = FromDxs(*source);
        const Image dst = FromDxs(*destination);
        return CopyRectangle(src, Rect(x, y, width, height), dst, static_cast<TEX_FILTER_FLAGS>(filter), xOffset, yOffset);
    });
}
