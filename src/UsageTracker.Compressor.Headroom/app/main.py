"""Headroom context-optimization sidecar.

A small FastAPI service that wraps the `headroom` library. It's source-agnostic: accept a list of
messages, return a compressed equivalent plus telemetry. Runs as a sidecar next to the backend, which
forwards to it only when its endpoint is configured (see RemoteCompressionForwarder)."""

from typing import Any

from fastapi import FastAPI
from pydantic import BaseModel

from app.headroom_service import HeadroomService

app = FastAPI(title="Headroom Context Optimizer")


class CompressRequest(BaseModel):
    messages: list[Any]
    model: str | None = None


@app.post("/compress")
def compress(request: CompressRequest) -> dict:
    return HeadroomService.compress_messages(request.messages, request.model)


@app.get("/health")
def health() -> dict:
    return {"status": "ok"}


# Future-friendly endpoints (not implemented yet) — this sidecar is a reusable context-optimization
# service, so downstream systems can grow into these without a new deployment:
#   POST /summarize   — summarize rather than losslessly compact
#   POST /optimize    — apply a configurable optimization pipeline
#   GET  /stats       — aggregate compression telemetry
