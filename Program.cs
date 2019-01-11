﻿/*
 * Copyright (c) 2017 Google Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not
 * use this file except in compliance with the License. You may obtain a copy of
 * the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations under
 * the License.
 */

using CommandLine;
using Google.Cloud.Speech.V1;
using NAudio.Wave;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Console = Colorful.Console;

namespace Mablae.LiveSubtitler
{
    [Verb("listen", HelpText = "Detects speech in a microphone input stream.")]
    class ListenOptions
    {
        [Option('l', "locale", HelpText = "Locale to use", Default = "de")]
        public string Locale { get; set; } = "de";
        
        [Option('t', "targetLanguage", HelpText = "Translation into this", Default = "de")]
        public string TargetLanguage { get; set; } = "de";

        [Option('s', "seconds", HelpText = "Number of seconds to listen for.", Default = 30)]
        public int Seconds { get; set; } = 30;
    }


    public class Program
    {
        private static bool keepRunning = true;
        private static WaveFormat waveFormat;
        private static WaveOutEvent outputDevice;
        private static WaveInEvent waveIn;


        private static CancellationTokenSource tokenCancelEarly;
        private static Transcriber transcriber;
        private static NdiRenderer ndiRenderer;


        static async Task<int> Loop(string locale, int seconds)
        {
            while (Program.keepRunning)
            {
                await transcriber.Run(seconds, locale);
            }

            return 0;
        }
        
        
        static async Task<object> StreamingMicRecognizeAsync(string locale, int seconds)
        {

            var loadDataTasks = new Task[]
                {
                    Task.Run(async () =>  await Loop(locale, seconds)),
                    Task.Run(async () =>  await ndiRenderer.Run())
                };

                try
                {
                    await Task.WhenAll(loadDataTasks);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
               
            waveIn.StopRecording();
            Console.WriteLine("exited gracefully");

            return 0;
        }


        public static void SetupDI()
        {
            var services = new ServiceCollection();

            var configurationBuilder = new ConfigurationBuilder();


            configurationBuilder.AddJsonFile("appsettings.json", optional: true);

            var configuration = configurationBuilder.Build();

            services.AddSingleton<IConfiguration>(configuration);

            var provider = services.BuildServiceProvider();

            var myConfig = provider.GetService<IConfiguration>();
        }


        public static int Main(string[] args)
        {
            SetupDI();


            var cancelNdi = new CancellationTokenSource();
            if (NAudio.Wave.WaveIn.DeviceCount < 1)
            {
                Console.WriteLine("No microphone!");
                return -1;
            }

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                cancelNdi.Cancel();
                Program.keepRunning = false;
                
            };

            waveFormat = new NAudio.Wave.WaveFormat(16000, 1);
            waveIn = new NAudio.Wave.WaveInEvent();
            waveIn.DeviceNumber = 0;
            waveIn.WaveFormat = waveFormat;

            outputDevice = new WaveOutEvent();

            //var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        //    mixer.ReadFully = false;
         //   mixer.AddMixerInput(new WaveInProvider(waveIn));

       //     outputDevice.Init(mixer);
        //    outputDevice.Play();

            waveIn.StartRecording();

            tokenCancelEarly = new CancellationTokenSource();

            transcriber = new Transcriber(waveIn, SpeechClient.Create());
            
            ndiRenderer = new NdiRenderer(cancelNdi.Token);
            var sourceLanguage = "en";
            var targetLanguage = "en";
            Parser.Default.ParseArguments<ListenOptions>(args)
                .WithParsed<ListenOptions>(o => { sourceLanguage = o.Locale;
                    targetLanguage = o.TargetLanguage;
                });
            
            
            Translator translator = new Translator();
            translator.TranslationReceived += delegate(object sender, TranslationReceivedEventArgs eventArgs)
                {
                    ndiRenderer.TranslatedText = eventArgs.Translation;
                };
            
            transcriber.PartialTranscriptionReceived += delegate(object sender, PartialTranscriptionReceivedEventArgs e)
                {
                    ndiRenderer.PartialText = e.Transcription;
                    
                };
            transcriber.CompleteTranscriptionReceived += delegate(object sender, CompleteTranscriptionReceivedEventArgs e)
                {
                    ndiRenderer.PartialText = e.Transcription;
                    Task.Run(async() => await translator.Translate(e.Transcription, sourceLanguage, targetLanguage));
                };

            return (int) Parser.Default.ParseArguments<
                ListenOptions
            >(args).MapResult(
                (ListenOptions opts) => StreamingMicRecognizeAsync(opts.Locale, opts.Seconds).Result,
                errs => 0);
        }
        
        
     
    }
    
    
   
}