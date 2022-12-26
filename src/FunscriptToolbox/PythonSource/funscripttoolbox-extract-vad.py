#@title Install and Import Dependencies

import json
import sys
import torch
from IPython.display import Audio
from pprint import pprint

torch.set_num_threads(1)

inputwav = sys.argv[1]
output = sys.argv[2]
sampling_rate = int(sys.argv[3])

model, utils = torch.hub.load(repo_or_dir='snakers4/silero-vad',
                              model='silero_vad',
                              force_reload=False,
                              onnx=False)

(get_speech_timestamps,
 save_audio,
 read_audio,
 VADIterator,
 collect_chunks) = utils

wav = read_audio(inputwav, sampling_rate=sampling_rate)
speech_timestamps = get_speech_timestamps(
        wav, 
        model, 
        sampling_rate=sampling_rate, 
        min_silence_duration_ms=500, 
        speech_pad_ms=30)

with open(output, 'w') as f:
    json.dump(speech_timestamps, f, indent=4)
