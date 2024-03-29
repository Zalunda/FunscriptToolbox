#include "stdafx.h"

/*
* Copyright (c) 2012 Stefano Sabatini
* Copyright (c) 2014 Clément Bœsch
*
* Permission is hereby granted, free of charge, to any person obtaining a copy
* of this software and associated documentation files (the "Software"), to deal
* in the Software without restriction, including without limitation the rights
* to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
* copies of the Software, and to permit persons to whom the Software is
* furnished to do so, subject to the following conditions:
*
* The above copyright notice and this permission notice shall be included in
* all copies or substantial portions of the Software.
*
* THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
* IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
* FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
* THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
* LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
* OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
* THE SOFTWARE.
*/

#pragma comment(lib, "avutil.lib")
#pragma comment(lib, "avcodec.lib")
#pragma comment(lib, "avformat.lib")

extern "C" {
#include <libavutil/error.h>
#include <libavutil/motion_vector.h>
#include <libavutil/timestamp.h>
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>	
}
#include <inttypes.h>
#include <fstream>
#include <thread>
#include <iostream>

static AVFormatContext *fmt_ctx = NULL;
static AVCodecContext *video_dec_ctx = NULL;
static AVStream *video_stream = NULL;
static const char *src_filename = NULL;

static int video_stream_idx = -1;
static AVFrame *frame = NULL;

std::ofstream writer;

static int blocSize = 16;

static bool initDone = false;
static double time_base_stream;
static double time_base_rate;
static int videoLength;
static int videoFrameCount = 0;
static int nbFrames;
static int nbBlocsX;
static int nbBlocsY;
static int nbBlocsTotal;

short* motionXinFrame;
short* motionYinFrame;
signed char* finalMotionXinFrame;
signed char* finalMotionYinFrame;
double lastTimestamp;

char for_future_use[100] = { 0 };

static int decode_packet(const AVPacket* pkt)
{
	int ret = avcodec_send_packet(video_dec_ctx, pkt);
	if (ret < 0) {
		// fprintf(stderr, "Error while sending a packet to the decoder: %s\n", av_err2str(ret));
		fprintf(stderr, "Error while sending a packet to the decoder\n");
		return ret;
	}

	while (ret >= 0) {
		ret = avcodec_receive_frame(video_dec_ctx, frame);
		if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF) {
			break;
		}
		else if (ret < 0) {
			// fprintf(stderr, "Error while receiving a frame from the decoder: %s\n", av_err2str(ret));
			fprintf(stderr, "Error while receiving a frame from the decoder\n");
			return ret;
		}

		if (!initDone)
		{
			// Write the header
			writer.put('F');
			writer.put('T');
			writer.put('M');
			writer.put('V');

			int formatVersion = 1;
			writer.write(reinterpret_cast<char*>(&formatVersion), sizeof(int));

			time_base_stream = av_q2d(video_stream->time_base) * 1000;
			videoLength = (int)std::round(video_stream->duration * time_base_stream);
			writer.write(reinterpret_cast<char*>(&videoLength), sizeof(int));
			fprintf(stderr, "videoLength: %d\n", videoLength);

			double frameRate = av_q2d(video_stream->avg_frame_rate);  // Or should I use r_frame_rate ??
			time_base_rate = 1 / frameRate;
			int simpleFrameRate = (int)(frameRate * 1000);
			writer.write(reinterpret_cast<char*>(&simpleFrameRate), sizeof(int));

			nbFrames = (int)video_stream->nb_frames;
			writer.write(reinterpret_cast<char*>(&nbFrames), sizeof(int));

			int frameWidth = video_dec_ctx->width;
			int frameHeight = video_dec_ctx->height;
			writer.write(reinterpret_cast<char*>(&frameWidth), sizeof(int));
			writer.write(reinterpret_cast<char*>(&frameHeight), sizeof(int));

			nbBlocsX = frameWidth / blocSize;
			nbBlocsY = frameHeight / blocSize;
			nbBlocsTotal = nbBlocsX * nbBlocsY;
			writer.write(reinterpret_cast<char*>(&nbBlocsX), sizeof(int));
			writer.write(reinterpret_cast<char*>(&nbBlocsY), sizeof(int));

			// Adding 'for future use' byte
			writer.write(for_future_use, 24); // Adding 'future use' bytes (file header size = 60)

			// create temporary buffer for frame data
			motionXinFrame = new short[nbBlocsTotal];
			motionYinFrame = new short[nbBlocsTotal];
			finalMotionXinFrame = new signed char[nbBlocsTotal];
			finalMotionYinFrame = new signed char[nbBlocsTotal];
			lastTimestamp = -1;
			initDone = true;
		}

		if (ret >= 0) {
			int i;
			AVFrameSideData* sd;

			memset(motionXinFrame, 0, nbBlocsTotal * sizeof(short));
			memset(motionYinFrame, 0, nbBlocsTotal * sizeof(short));

			sd = av_frame_get_side_data(frame, AV_FRAME_DATA_MOTION_VECTORS);
			if (sd) {

				const AVMotionVector* mvs = (const AVMotionVector*)sd->data;
				for (i = 0; i < sd->size / sizeof(*mvs); i++) {
					const AVMotionVector* mv = &mvs[i];
					if ((mv->motion_x != 0 || mv->motion_y != 0))
					{
						int indexX = mv->dst_x / blocSize;
						int indexY = mv->dst_y / blocSize;
						if (indexX < nbBlocsX && indexY < nbBlocsY) {
							int multiplicator = (mv->w * mv->h) / (8 * 8);
							int index = indexY * nbBlocsX + indexX;
							int mX = mv->motion_x * multiplicator;
							motionXinFrame[index] += (mv->source < 0 ? -mX : mX);
							int mY = mv->motion_y * multiplicator;
							motionYinFrame[index] += (mv->source < 0 ? -mY : mY);
						}
					}
				}
			}

			double timestamp_ms = (frame->pts * time_base_stream);
			double next_timestamp_ms = ((frame->pts + frame->duration) * time_base_stream);
			if (timestamp_ms < lastTimestamp)
			{
				fprintf(stderr, "Out of order frame: %f < %f\n", timestamp_ms, lastTimestamp);
				return -10000;
			}
			int calculatedFrameNumber = (int) std::round(timestamp_ms / time_base_rate / 1000);
			if (videoFrameCount != calculatedFrameNumber)
			{
				fprintf(stderr, "Invalid calculated Frame Number: %d != %d.\n", calculatedFrameNumber, videoFrameCount);
				return -10001;
			}
			int timestamp_ms_as_int = (int) timestamp_ms;
			writer.write(reinterpret_cast<char*>(&calculatedFrameNumber), sizeof(int));
			writer.write(reinterpret_cast<char*>(&timestamp_ms_as_int), sizeof(int));
			char pict_type_char = av_get_picture_type_char(frame->pict_type);
			writer.write(&pict_type_char, sizeof(char));
			writer.write(for_future_use, 11); // Adding 'future use' bytes (frame header size = 20)
			lastTimestamp = timestamp_ms;

			int total = 0;
			for (int i = 0; i < nbBlocsTotal; i++)
			{
				// Should I ignore really large movement ? i.e. if motionXinFrame[i] / ((16 * 16) / (8 * 8) > 127 =>  0, 0
				finalMotionXinFrame[i] = (signed char)(std::max(std::min(127, motionXinFrame[i] / ((16 * 16) / (8 * 8))), -127));
				finalMotionYinFrame[i] = (signed char)(std::max(std::min(127, motionYinFrame[i] / ((16 * 16) / (8 * 8))), -127));
				total += finalMotionXinFrame[i];
				total += finalMotionYinFrame[i];
			}

			writer.write((char*)finalMotionXinFrame, nbBlocsTotal);
			writer.write((char*)finalMotionYinFrame, nbBlocsTotal);

			videoFrameCount++;
			if (videoFrameCount % 1 == 0)
			{
				printf("Progress,%d,%d,%d,%d\n", calculatedFrameNumber, timestamp_ms_as_int, videoLength, total);
			}
			av_frame_unref(frame);
		}
	}

	return 0;
}

static int open_codec_context(AVFormatContext* fmt_ctx, enum AVMediaType type)
{
	int ret;
	AVStream* st;
	AVCodecContext* dec_ctx = NULL;
	const AVCodec* dec = NULL;
	AVDictionary* opts = NULL;

	ret = av_find_best_stream(fmt_ctx, type, -1, -1, &dec, 0);
	if (ret < 0) {
		fprintf(stderr, "Could not find %s stream in input file '%s'\n",
			av_get_media_type_string(type), src_filename);
		return ret;
	}
	else {
		int stream_idx = ret;
		st = fmt_ctx->streams[stream_idx];

		dec_ctx = avcodec_alloc_context3(dec);
		if (!dec_ctx) {
			fprintf(stderr, "Failed to allocate codec\n");
			return AVERROR(EINVAL);
		}

		ret = avcodec_parameters_to_context(dec_ctx, st->codecpar);
		if (ret < 0) {
			fprintf(stderr, "Failed to copy codec parameters to codec context\n");
			return ret;
		}

		// set codec to automatically determine how many threads suits best for the decoding job
		dec_ctx->thread_count = 0;

		if (dec->capabilities & AV_CODEC_CAP_FRAME_THREADS)
			dec_ctx->thread_type = FF_THREAD_FRAME;
		else if (dec->capabilities & AV_CODEC_CAP_SLICE_THREADS)
			dec_ctx->thread_type = FF_THREAD_SLICE;
		else
			dec_ctx->thread_count = 1; //don't use multithreading

		/* Init the video decoder */
		av_dict_set(&opts, "flags2", "+export_mvs", 0);
		ret = avcodec_open2(dec_ctx, dec, &opts);
		av_dict_free(&opts);
		if (ret < 0) {
			fprintf(stderr, "Failed to open %s codec\n",
				av_get_media_type_string(type));
			return ret;
		}

		video_stream_idx = stream_idx;
		video_stream = fmt_ctx->streams[video_stream_idx];
		video_dec_ctx = dec_ctx;
	}

	return 0;
}

int main(int argc, char** argv)
{
	int ret = 0;
	AVPacket* pkt = NULL;

	if (argc != 3) {
		fprintf(stderr, "Usage: %s <video> <outputMvs>\n", argv[0]);
		exit(1);
	}
	src_filename = argv[1];
	writer.open(argv[2], std::ios::out | std::ios::trunc | std::ios::binary);

	if (avformat_open_input(&fmt_ctx, src_filename, NULL, NULL) < 0) {
		fprintf(stderr, "Could not open source file %s\n", src_filename);
		exit(1);
	}

	if (avformat_find_stream_info(fmt_ctx, NULL) < 0) {
		fprintf(stderr, "Could not find stream information\n");
		exit(1);
	}

	open_codec_context(fmt_ctx, AVMEDIA_TYPE_VIDEO);

	av_dump_format(fmt_ctx, 0, src_filename, 0);

	clock_t start = clock();

	if (!video_stream) {
		fprintf(stderr, "Could not find video stream in the input, aborting\n");
		ret = 1;
		goto end;
	}

	frame = av_frame_alloc();
	if (!frame) {
		fprintf(stderr, "Could not allocate frame\n");
		ret = AVERROR(ENOMEM);
		goto end;
	}

	pkt = av_packet_alloc();
	if (!pkt) {
		fprintf(stderr, "Could not allocate AVPacket\n");
		ret = AVERROR(ENOMEM);
		goto end;
	}

	/* read frames from the file */
	while (av_read_frame(fmt_ctx, pkt) >= 0) {
		if (pkt->stream_index == video_stream_idx)
			ret = decode_packet(pkt);
		av_packet_unref(pkt);
		if (ret < 0)
			break;
	}

	/* flush cached frames */
	decode_packet(NULL);

end:
	writer.close();
	clock_t end = clock();

	// Calculate elapsed time
	double elapsed_time = (double)(end - start) / CLOCKS_PER_SEC;
	fprintf(stderr, "Elapsed time: %f seconds\n", elapsed_time);

	avcodec_free_context(&video_dec_ctx);
	avformat_close_input(&fmt_ctx);
	av_frame_free(&frame);
	av_packet_free(&pkt);

	return ret < 0;
}
