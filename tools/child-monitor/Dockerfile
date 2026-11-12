FROM python:3.11-slim-bookworm
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && pip install --no-cache-dir numpy==1.26.4 tflite-runtime==2.14.0
WORKDIR /app
COPY --chmod=0444 monitor.py yamnet.tflite yamnet_class_map.csv /app/
USER 65534:65534
ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 OPENBLAS_NUM_THREADS=1
ENTRYPOINT ["python", "/app/monitor.py"]
