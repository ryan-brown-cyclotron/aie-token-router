from headroom import compress
from headroom.compress import CompressConfig

# Aggressive by design: this sidecar preserves nothing. The daemon forwards a single tool output and
# wants it compacted as hard as possible, so we disable every protection Headroom applies by default:
#   - compress_user_messages: the forwarder wraps the tool output as a `user` message, and those are
#     protected by default - without this the output is never touched.
#   - protect_recent=0: no "recent messages" protection window.
#   - protect_analysis_context=False: don't spare analysis/reasoning context.
_AGGRESSIVE_CONFIG = CompressConfig(
    compress_user_messages=True,
    compress_system_messages=True,
    protect_recent=0,
    protect_analysis_context=False,
)


class HeadroomService:
    """Thin wrapper over the Headroom (`headroom-ai`) library. Keeps the HTTP boundary free of library
    specifics and normalizes the `CompressResult` into a stable JSON-serializable shape with telemetry."""

    @staticmethod
    def compress_messages(messages: list, model: str | None = None) -> dict:
        kwargs: dict = {"config": _AGGRESSIVE_CONFIG}
        if model:
            kwargs["model"] = model

        result = compress(messages, **kwargs)

        return {
            "messages": getattr(result, "messages", messages),
            "tokens_saved": getattr(result, "tokens_saved", None),
            "tokens_before": getattr(result, "tokens_before", None),
            "tokens_after": getattr(result, "tokens_after", None),
            "compression_ratio": getattr(result, "compression_ratio", None),
        }
