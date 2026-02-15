using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Silk.NET.OpenGL;
using System;
using System.Diagnostics;

namespace ASE
{
    public class GLControl : OpenGlControlBase
    {
        private const string VertexShaderSource = @"#version 100
            precision highp float;

            // Atributos de entrada del vértice
            attribute vec2 aPos; // Posición del vértice
            attribute vec2 aTexCoord; // Coordenadas de textura

            // Variable de salida para el fragment shader
            varying vec2 v_texCoord;

            void main() {
                // Calcula la posición del vértice en el espacio de clip
                gl_Position = vec4(aPos, 0.0, 1.0);
                // Pasa las coordenadas de textura al fragment shader
                v_texCoord  = aTexCoord;
            }";

        private const string FragmentShaderSource = @"#version 100
                precision highp float;

                // Coordenadas interpoladas desde el vertex shader
                varying vec2 v_texCoord;
                uniform sampler2D uTexture; // Textura de entrada

                // Tamaños (en píxeles)
                uniform vec2 uSourceSize; // Tamaño de la textura fuente
                uniform vec2 uOutputSize; // Tamaño de la salida (viewport)

                uniform float uTime; // Tiempo para animaciones

                // Controles
                uniform float uCurvature; // Curvatura de la pantalla
                uniform float uVignette; // Intensidad de la viñeta
                uniform float uScanline; // Intensidad de las líneas de escaneo
                uniform float uChromAb; // Aberración cromática
                uniform float uBloom; // Intensidad del bloom
                uniform float uMask; // Intensidad de la máscara de puntos
                uniform float uNoise; // Intensidad del ruido

                // Genera un valor pseudoaleatorio basado en una posición 2D
                float hash12(vec2 p) {
                    vec3 p3  = fract(vec3(p.xyx) * 0.1031);
                    p3 += dot(p3, p3.yzx + 33.33);
                    return fract((p3.x + p3.y) * p3.z);
                }

                // Obtiene el color RGB de la textura en las coordenadas UV
                vec3 texRGB(vec2 uv) {
                    return texture2D(uTexture, uv).rgb;
                }

                void main() {
                    // Coordenadas UV iniciales
                    vec2 uv = v_texCoord;
                    float aspect = uOutputSize.x / uOutputSize.y; // Relación de aspecto

                    // --- 1. Curvatura de la pantalla ---
                    vec2 p = uv * 2.0 - 1.0; // Convierte UV a espacio normalizado [-1, 1]
                    p.x *= aspect; // Ajusta por la relación de aspecto

                    float r2 = dot(p, p); // Distancia al centro al cuadrado
                    p *= (1.0 + uCurvature * r2); // Aplica la curvatura

                    p.x /= aspect; // Reajusta por la relación de aspecto
                    uv = p * 0.5 + 0.5; // Vuelve a espacio UV [0, 1]

                    // --- 2. Máscara de pantalla (bordes negros) ---
                    float inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);

                    float feather = 0.010; // Suavizado de los bordes
                    float edge =
                        smoothstep(0.0, feather, uv.x) *
                        smoothstep(0.0, feather, uv.y) *
                        smoothstep(0.0, feather, 1.0 - uv.x) *
                        smoothstep(0.0, feather, 1.0 - uv.y);

                    float screenMask = inside * edge; // Máscara final

                    // --- 3. Aberración cromática ---
                    float dist = sqrt(r2); // Distancia al centro
                    vec2 dir = normalize(p + vec2(1e-4)); // Dirección desde el centro

                    float ca = uChromAb * (0.0006 + 0.0018 * dist * dist); // Magnitud de la aberración
                    vec2 caOff = dir * ca; // Desplazamiento por canal

                    vec3 col;
                    col.r = texture2D(uTexture, uv + caOff).r; // Canal rojo desplazado
                    col.g = texture2D(uTexture, uv).g; // Canal verde sin desplazar
                    col.b = texture2D(uTexture, uv - caOff).b; // Canal azul desplazado

                    // --- 4. Bloom (resplandor) ---
                    vec3 lin = pow(col, vec3(2.2)); // Corrección gamma inversa

                    vec2 t = 1.0 / max(uSourceSize, vec2(1.0)); // Tamaño del texel
                    vec3 b =
                        texRGB(uv) * 0.40 +
                        texRGB(uv + vec2( t.x, 0.0)) * 0.15 +
                        texRGB(uv + vec2(-t.x, 0.0)) * 0.15 +
                        texRGB(uv + vec2(0.0,  t.y)) * 0.15 +
                        texRGB(uv + vec2(0.0, -t.y)) * 0.15;

                    b = pow(b, vec3(2.2)); // Corrección gamma inversa
                    vec3 bright = max(b - vec3(0.60), vec3(0.0)); // Brillo adicional
                    lin += bright * (1.0 * uBloom); // Aplica el bloom

                    // --- 5. Scanlines (líneas de escaneo) ---
                    float y = uv.y * uSourceSize.y; // Coordenada Y en píxeles
                    float scan = 0.92 + 0.08 * sin(6.2831853 * (y + 0.15)); // Patrón de líneas
                    lin *= mix(1.0, scan, uScanline); // Aplica las líneas de escaneo

                    // --- 6. Dot-mask (rejilla de puntos) ---
                    float px = floor(gl_FragCoord.x); // Coordenada X del fragmento
                    float py = floor(gl_FragCoord.y); // Coordenada Y del fragmento
                    float row = mod(py, 2.0); // Fila par o impar
                    float shift = (row > 0.5) ? 1.5 : 0.0; // Desplazamiento en filas impares
                    float parts = mod(px + shift, 3.0); // Patrón de puntos RGB

                    vec3 dots = vec3(0.25); // Color base de la rejilla (gris oscuro)
                    if (parts < 1.0) dots.r = 1.0; // Punto rojo
                    else if (parts < 2.0) dots.g = 1.0; // Punto verde
                    else dots.b = 1.0; // Punto azul

                    lin *= mix(vec3(1.0), dots, uMask); // Aplica la máscara de puntos

                    // --- 7. Viñeta ---
                    float vig = 1.0 - uVignette * smoothstep(0.55, 1.25, dist); // Atenuación en bordes
                    lin *= vig; // Aplica la viñeta

                    // --- 8. Ruido ---
                    float n = hash12(gl_FragCoord.xy + uTime * 60.0); // Genera ruido
                    lin += (n - 0.5) * 0.02 * uNoise; // Aplica el ruido

                    // --- 9. Salida final ---
                    vec3 outCol = pow(max(lin, 0.0), vec3(1.0 / 2.2)); // Corrección gamma
                    outCol = mix(vec3(0.0), outCol, screenMask); // Aplica la máscara de pantalla

                    gl_FragColor = vec4(outCol, 1.0); // Color final del fragmento
                }";

        private GL _gl;
        private uint _textureId;
        private uint _program;
        private uint _vao;
        private uint _vbo;

        // uniforms: cache de locations
        private int _uTextureLoc;
        private int _uSourceSizeLoc;
        private int _uOutputSizeLoc;
        private int _uTimeLoc;
        private int _uCurvatureLoc;
        private int _uVignetteLoc;
        private int _uScanlineLoc;
        private int _uChromAbLoc;
        private int _uBloomLoc;
        private int _uMaskLoc;
        private int _uNoiseLoc;

        private readonly Stopwatch _timer = new Stopwatch();

        // Atari screen size
        private const int SrcW = ASEMain.ScreenWidth;
        private const int SrcH = ASEMain.ScreenHeight / 2;

        private readonly byte[] pixels = new byte[SrcW * SrcH * 4];

        private void CheckShader(uint shader, string name)
        {
            _gl.GetShader(shader, GLEnum.CompileStatus, out int status);
            if (status == 0)
            {
                string log = _gl.GetShaderInfoLog(shader);
                Console.WriteLine($"GLERROR {name}: {log}");
            }
        }

        private void CacheUniformLocations()
        {
            _uTextureLoc = _gl.GetUniformLocation(_program, "uTexture");
            _uSourceSizeLoc = _gl.GetUniformLocation(_program, "uSourceSize");
            _uOutputSizeLoc = _gl.GetUniformLocation(_program, "uOutputSize");
            _uTimeLoc = _gl.GetUniformLocation(_program, "uTime");

            _uCurvatureLoc = _gl.GetUniformLocation(_program, "uCurvature");
            _uVignetteLoc = _gl.GetUniformLocation(_program, "uVignette");
            _uScanlineLoc = _gl.GetUniformLocation(_program, "uScanline");
            _uChromAbLoc = _gl.GetUniformLocation(_program, "uChromAb");
            _uBloomLoc = _gl.GetUniformLocation(_program, "uBloom");
            _uMaskLoc = _gl.GetUniformLocation(_program, "uMask");
            _uNoiseLoc = _gl.GetUniformLocation(_program, "uNoise");
        }

        protected override unsafe void OnOpenGlInit(GlInterface gl)
        {
            _gl = GL.GetApi(gl.GetProcAddress);

            uint vs = _gl.CreateShader(GLEnum.VertexShader);
            _gl.ShaderSource(vs, VertexShaderSource);
            _gl.CompileShader(vs);
            CheckShader(vs, "Vertex Shader");

            uint fs = _gl.CreateShader(GLEnum.FragmentShader);
            _gl.ShaderSource(fs, FragmentShaderSource);
            _gl.CompileShader(fs);
            CheckShader(fs, "Fragment Shader");

            _program = _gl.CreateProgram();
            _gl.AttachShader(_program, vs);
            _gl.AttachShader(_program, fs);

            _gl.BindAttribLocation(_program, 0, "aPos");
            _gl.BindAttribLocation(_program, 1, "aTexCoord");

            _gl.LinkProgram(_program);
            _gl.GetProgram(_program, GLEnum.LinkStatus, out int linkStatus);
            if (linkStatus == 0)
                Console.WriteLine($"ERROR LINK: {_gl.GetProgramInfoLog(_program)}");

            // VAO/VBO
            float[] vertices =
            {
                -1.0f,  1.0f,  0.0f, 0.0f,
                -1.0f, -1.0f,  0.0f, 1.0f,
                 1.0f, -1.0f,  1.0f, 1.0f,
                 1.0f,  1.0f,  1.0f, 0.0f
            };

            _vao = _gl.GenVertexArray();
            _gl.BindVertexArray(_vao);

            _vbo = _gl.GenBuffer();
            _gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);

            fixed (void* v = vertices)
                _gl.BufferData(GLEnum.ArrayBuffer, (uint)(vertices.Length * sizeof(float)), v, GLEnum.StaticDraw);

            _gl.VertexAttribPointer(0, 2, GLEnum.Float, false, 4 * sizeof(float), (void*)0);
            _gl.EnableVertexAttribArray(0);

            _gl.VertexAttribPointer(1, 2, GLEnum.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
            _gl.EnableVertexAttribArray(1);

            _gl.BindVertexArray(0);

            // Textura
            _textureId = _gl.GenTexture();
            _gl.BindTexture(GLEnum.Texture2D, _textureId);

            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Linear); // TODO: Opción para configurar si Nearest o Linear
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);

            _gl.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba, SrcW, SrcH, 0,
                           GLEnum.Rgba, GLEnum.UnsignedByte, null);

            _gl.GenerateMipmap(GLEnum.Texture2D);
            
            // Cache uniforms + set constantes
            _gl.UseProgram(_program);
            CacheUniformLocations();

            // sampler fijo a la unidad 0
            if (_uTextureLoc >= 0)
                _gl.Uniform1(_uTextureLoc, 0);

            if (_uSourceSizeLoc >= 0)
                _gl.Uniform2(_uSourceSizeLoc, (float)SrcW, (float)SrcH);

            _timer.Restart();
        }

        protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (_gl == null || _program == 0) return;

            _gl.BindFramebuffer(GLEnum.Framebuffer, (uint)fb);

            uint outW = (uint)Math.Max(1, (int)Bounds.Width);
            uint outH = (uint)Math.Max(1, (int)Bounds.Height);
            _gl.Viewport(0, 0, outW, outH);

            _gl.Disable(GLEnum.CullFace);   
            _gl.Disable(GLEnum.DepthTest);

            _gl.ClearColor(0.1f, 0.1f, 0.1f, 1.0f);
            _gl.Clear((uint)GLEnum.ColorBufferBit);

            _gl.UseProgram(_program);
            _gl.BindVertexArray(_vao);

            // Textura en unidad 0
            _gl.ActiveTexture(GLEnum.Texture0);
            _gl.BindTexture(GLEnum.Texture2D, _textureId);

            // uniforms por frame
            float time = (float)_timer.Elapsed.TotalSeconds;

            if (_uOutputSizeLoc >= 0)
                _gl.Uniform2(_uOutputSizeLoc, (float)outW, (float)outH);

            if (_uTimeLoc >= 0)
                _gl.Uniform1(_uTimeLoc, time);

            if (_uCurvatureLoc >= 0) _gl.Uniform1(_uCurvatureLoc, Config.ConfigOptions.RunninConfig.Curvature);
            if (_uVignetteLoc >= 0) _gl.Uniform1(_uVignetteLoc, Config.ConfigOptions.RunninConfig.Vignette);
            if (_uScanlineLoc >= 0) _gl.Uniform1(_uScanlineLoc, Config.ConfigOptions.RunninConfig.Scanline);
            if (_uChromAbLoc >= 0) _gl.Uniform1(_uChromAbLoc, Config.ConfigOptions.RunninConfig.ChromAb);
            if (_uBloomLoc >= 0) _gl.Uniform1(_uBloomLoc, Config.ConfigOptions.RunninConfig.Bloom);
            if (_uMaskLoc >= 0) _gl.Uniform1(_uMaskLoc, Config.ConfigOptions.RunninConfig.Mask);
            if (_uNoiseLoc >= 0) _gl.Uniform1(_uNoiseLoc, Config.ConfigOptions.RunninConfig.Noise);

            // Subir píxeles
            // ObtenerPixelesRGBA se encarga de copiar los píxeles en el buffer del bucle principal
            // El bucle principal debería tener un método de copia, esto es sólo temporal.
            byte[] pixelData = ObtenerPixelesRGBA();
            fixed (void* pData = pixelData)
            {
                _gl.TexSubImage2D(GLEnum.Texture2D, 0, 0, 0, SrcW, SrcH,
                                  GLEnum.Rgba , GLEnum.UnsignedByte, pData);
            }

            _gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

            RequestNextFrameRendering();
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            _gl?.DeleteBuffer(_vbo);
            _gl?.DeleteVertexArray(_vao);
            _gl?.DeleteTexture(_textureId);
            _gl?.DeleteProgram(_program);
            base.OnOpenGlDeinit(gl);
        }

        private byte[] ObtenerPixelesRGBA()
        {
            System.Buffer.BlockCopy(ASEMain.ScreenBuffer, 0, pixels, 0, pixels.Length);
            return pixels;
        }
    }
}
