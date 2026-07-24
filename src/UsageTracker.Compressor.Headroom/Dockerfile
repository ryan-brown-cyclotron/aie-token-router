# Headroom context-optimization sidecar (FastAPI + uvicorn + ML compression).
# headroom-ai[ml] is large (~2GB with torch+transformers); uses a multi-stage build to keep final image lean.
FROM python:3.11 AS builder

RUN pip install --user --no-cache-dir --upgrade pip setuptools
COPY requirements.txt .
RUN pip install --user --no-cache-dir -r requirements.txt


FROM python:3.11-slim

COPY --from=builder /root/.local /root/.local
ENV PATH=/root/.local/bin:$PATH

WORKDIR /srv
COPY app ./app

EXPOSE 8000

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
