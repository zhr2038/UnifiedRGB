"""Download the published TensorFlow model and class map after checking their hashes."""

from hashlib import sha256
from pathlib import Path
from urllib.request import urlopen


HERE = Path(__file__).resolve().parent
FILES = {
    "yamnet.tflite": (
        "https://raw.githubusercontent.com/tensorflow/tflite-support/92baa6a159aadc1aafb8cc4b7889ed9608bb14a8/"
        "tensorflow_lite_support/metadata/python/tests/testdata/audio_classifier/yamnet_wavin_quantized_mel_relu6.tflite",
        "b8cc7ebd9edf3c16fcd79230e4c105316e8872c51ce447348176f75d6d35d570",
    ),
    "yamnet_class_map.csv": (
        "https://raw.githubusercontent.com/tensorflow/models/19f287775ae950e22a9b5fb82727e72cff2e3664/"
        "research/audioset/yamnet/yamnet_class_map.csv",
        "cdf24d193e196d9e95912a2667051ae203e92a2ba09449218ccb40ef787c6df2",
    ),
}


def main():
    for name, (url, digest) in FILES.items():
        path = HERE / name
        if path.exists():
            if sha256(path.read_bytes()).hexdigest() != digest:
                raise ValueError(f"{name} exists and does not match the documented version")
        else:
            with urlopen(url, timeout=30) as response:
                data = response.read(8 * 1024 * 1024)
            if sha256(data).hexdigest() != digest:
                raise ValueError(f"{name} download checksum mismatch")
            with path.open("xb") as output:
                output.write(data)
        print(f"Verified {name}")


if __name__ == "__main__":
    main()
