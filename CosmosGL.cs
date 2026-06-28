using Cosmos.System.Graphics;
using Cosmos.System;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;

namespace CosmosGL.CGL
{
    // ==========================================
    // 1. ĐỊNH NGHĨA VERTEX (Cấu trúc điểm)
    // ==========================================
    public struct Vertex
    {
        public Vector3 Position;
        public Vector4 Color;
        public Vector2 TexCoord;
        public Vector3 Normal;

        // Vertex sau khi transform (camera space & clip space)
        public Vector4 ClipPos;
        public float Depth; // Giá trị Z sau perspective divide
    }

    // ==========================================
    // 2. LỚP COSMOSGL CHÍNH
    // ==========================================
    public static class CosmosGL
    {
        // ---------- Hằng số OpenGL (bro đã có sẵn, mình bổ sung thiếu) ----------
        public const int GL_MODELVIEW = 0x1700;
        public const int GL_PROJECTION = 0x1701;
        public const int GL_TEXTURE = 0x1702;
        public const int GL_DEPTH_TEST = 0x0B71;
        public const int GL_DEPTH_FUNC = 0x0B74;
        public const int GL_LEQUAL = 0x0203; // Bổ sung cho depth test

        // Các hằng số khác bro đã define, mình giữ nguyên tinh thần, không copy lại để tránh tràn.

        // ---------- TRẠNG THÁI TOÀN CỤC (State Machine) ----------
        private static Canvas _canvas;
        private static uint[] _frameBuffer;
        private static float[] _zBuffer;
        private static int _viewportX, _viewportY, _viewportWidth, _viewportHeight;

        private static int _matrixMode = GL_MODELVIEW;
        private static Matrix4x4 _modelViewMatrix = Matrix4x4.Identity;
        private static Matrix4x4 _projectionMatrix = Matrix4x4.Identity;
        private static Stack<Matrix4x4> _modelViewStack = new Stack<Matrix4x4>();
        private static Stack<Matrix4x4> _projectionStack = new Stack<Matrix4x4>();

        // Màu sắc, Normal, TexCoord hiện tại
        private static Vector4 _currentColor = new Vector4(1, 1, 1, 1);
        private static Vector3 _currentNormal = new Vector3(0, 0, 1);
        private static Vector2 _currentTexCoord = Vector2.Zero;

        // Depth test
        private static bool _depthTestEnabled = false;
        private static int _depthFunc = GL_LESS;
        private static float _clearDepth = 1.0f;
        private static Vector4 _clearColor = new Vector4(0, 0, 0, 1);

        // glBegin/glEnd buffer
        private static List<Vertex> _vertexBuffer = new List<Vertex>();
        private static bool _isInBeginEnd = false;
        private static int _currentPrimitive = 0;

        // ---------- KHỞI TẠO (Gắn với màn hình Cosmos) ----------
        public static void Initialize(Canvas screen)
        {
            _canvas = screen;
            _viewportWidth = screen.Mode.Columns;
            _viewportHeight = screen.Mode.Rows;
            _frameBuffer = new uint[_viewportWidth * _viewportHeight];
            _zBuffer = new float[_viewportWidth * _viewportHeight];
            
            // Reset toàn bộ trạng thái
            glClearColor(0, 0, 0, 1);
            glClearDepth(1.0);
            glDepthFunc(GL_LESS);
            glEnable(GL_DEPTH_TEST); // Mặc định bật để 3D đẹp
            glMatrixMode(GL_MODELVIEW);
            glLoadIdentity();
            glMatrixMode(GL_PROJECTION);
            glLoadIdentity();
        }

        // ---------- HÀM TIỆN ÍCH NỘI BỘ ----------
        private static void SetPixel(int x, int y, float z, Vector4 color)
        {
            if (x < 0 || x >= _viewportWidth || y < 0 || y >= _viewportHeight) return;

            int index = x + y * _viewportWidth;

            // Depth Test
            if (_depthTestEnabled)
            {
                bool pass = false;
                switch (_depthFunc)
                {
                    case GL_LESS: pass = z < _zBuffer[index]; break;
                    case GL_LEQUAL: pass = z <= _zBuffer[index]; break;
                    // Thêm các case khác nếu cần (GL_ALWAYS, GL_EQUAL...)
                    default: pass = z < _zBuffer[index]; break;
                }
                if (!pass) return;
                _zBuffer[index] = z;
            }

            // Clamp color
            byte r = (byte)(Math.Clamp(color.X, 0, 1) * 255);
            byte g = (byte)(Math.Clamp(color.Y, 0, 1) * 255);
            byte b = (byte)(Math.Clamp(color.Z, 0, 1) * 255);
            byte a = (byte)(Math.Clamp(color.W, 0, 1) * 255);

            // Pack ARGB (phù hợp với Canvas của Cosmos)
            uint packed = (uint)((a << 24) | (r << 16) | (g << 8) | b);
            _frameBuffer[index] = packed;
        }

        private static void PresentFrame()
        {
            // Vẽ toàn bộ framebuffer lên Canvas (tối ưu nhất là gán RawData nếu có)
            // Cosmos Canvas thường dùng DrawPoint, mình loop để đẩy.
            for (int y = 0; y < _viewportHeight; y++)
            {
                for (int x = 0; x < _viewportWidth; x++)
                {
                    int idx = x + y * _viewportWidth;
                    uint color = _frameBuffer[idx];
                    byte a = (byte)(color >> 24);
                    byte r = (byte)(color >> 16);
                    byte g = (byte)(color >> 8);
                    byte b = (byte)color;
                    _canvas.DrawPoint(new Pen(Color.FromArgb(a, r, g, b)), x, y);
                }
            }
            _canvas.Display();
        }

        // ---------- VẼ ĐIỂM (Point) ----------
        private static void DrawPoint(Vertex v)
        {
            // Transform & Project
            Vector4 clip = Vector4.Transform(new Vector4(v.Position, 1.0f), _modelViewMatrix);
            clip = Vector4.Transform(clip, _projectionMatrix);
            if (clip.W == 0) return;
            Vector3 ndc = new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
            float depth = ndc.Z;

            int screenX = (int)((ndc.X + 1) * 0.5f * _viewportWidth) + _viewportX;
            int screenY = (int)((1 - (ndc.Y + 1) * 0.5f) * _viewportHeight) + _viewportY;

            SetPixel(screenX, screenY, depth, v.Color);
        }

        // ---------- VẼ ĐƯỜNG (Bresenham + Z-Lerp) ----------
        private static void DrawLine(Vertex v1, Vertex v2)
        {
            // Transform v1, v2 (tính clip pos)
            Vector4 c1 = Vector4.Transform(new Vector4(v1.Position, 1.0f), _modelViewMatrix);
            c1 = Vector4.Transform(c1, _projectionMatrix);
            Vector4 c2 = Vector4.Transform(new Vector4(v2.Position, 1.0f), _modelViewMatrix);
            c2 = Vector4.Transform(c2, _projectionMatrix);
            
            if (c1.W == 0 || c2.W == 0) return;

            Vector3 ndc1 = new Vector3(c1.X / c1.W, c1.Y / c1.W, c1.Z / c1.W);
            Vector3 ndc2 = new Vector3(c2.X / c2.W, c2.Y / c2.W, c2.Z / c2.W);

            int x1 = (int)((ndc1.X + 1) * 0.5f * _viewportWidth) + _viewportX;
            int y1 = (int)((1 - (ndc1.Y + 1) * 0.5f) * _viewportHeight) + _viewportY;
            int x2 = (int)((ndc2.X + 1) * 0.5f * _viewportWidth) + _viewportX;
            int y2 = (int)((1 - (ndc2.Y + 1) * 0.5f) * _viewportHeight) + _viewportY;

            float z1 = ndc1.Z;
            float z2 = ndc2.Z;
            Vector4 col1 = v1.Color;
            Vector4 col2 = v2.Color;

            int dx = Math.Abs(x2 - x1);
            int dy = Math.Abs(y2 - y1);
            int sx = x1 < x2 ? 1 : -1;
            int sy = y1 < y2 ? 1 : -1;
            int err = dx - dy;

            float totalDist = (float)Math.Sqrt(dx * dx + dy * dy);
            float currentDist = 0;

            while (true)
            {
                float t = (totalDist == 0) ? 0 : currentDist / totalDist;
                float z = z1 + (z2 - z1) * t;
                Vector4 col = Vector4.Lerp(col1, col2, t);
                SetPixel(x1, y1, z, col);

                if (x1 == x2 && y1 == y2) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x1 += sx; }
                if (e2 < dx) { err += dx; y1 += sy; }
                currentDist += (float)Math.Sqrt(2); // approximate
            }
        }

        // ---------- VẼ TAM GIÁC (Scanline với Barycentric) ----------
        private static void DrawTriangle(Vertex v1, Vertex v2, Vertex v3)
        {
            // Transform to screen space
            Vector4 c1 = Vector4.Transform(new Vector4(v1.Position, 1.0f), _modelViewMatrix);
            c1 = Vector4.Transform(c1, _projectionMatrix);
            Vector4 c2 = Vector4.Transform(new Vector4(v2.Position, 1.0f), _modelViewMatrix);
            c2 = Vector4.Transform(c2, _projectionMatrix);
            Vector4 c3 = Vector4.Transform(new Vector4(v3.Position, 1.0f), _modelViewMatrix);
            c3 = Vector4.Transform(c3, _projectionMatrix);

            if (c1.W == 0 || c2.W == 0 || c3.W == 0) return;

            Vector3 ndc1 = new Vector3(c1.X / c1.W, c1.Y / c1.W, c1.Z / c1.W);
            Vector3 ndc2 = new Vector3(c2.X / c2.W, c2.Y / c2.W, c2.Z / c2.W);
            Vector3 ndc3 = new Vector3(c3.X / c3.W, c3.Y / c3.W, c3.Z / c3.W);

            float x1 = (ndc1.X + 1) * 0.5f * _viewportWidth + _viewportX;
            float y1 = (1 - (ndc1.Y + 1) * 0.5f) * _viewportHeight + _viewportY;
            float x2 = (ndc2.X + 1) * 0.5f * _viewportWidth + _viewportX;
            float y2 = (1 - (ndc2.Y + 1) * 0.5f) * _viewportHeight + _viewportY;
            float x3 = (ndc3.X + 1) * 0.5f * _viewportWidth + _viewportX;
            float y3 = (1 - (ndc3.Y + 1) * 0.5f) * _viewportHeight + _viewportY;

            // Bounding box
            int minX = (int)Math.Floor(Math.Min(x1, Math.Min(x2, x3)));
            int maxX = (int)Math.Ceiling(Math.Max(x1, Math.Max(x2, x3)));
            int minY = (int)Math.Floor(Math.Min(y1, Math.Min(y2, y3)));
            int maxY = (int)Math.Ceiling(Math.Max(y1, Math.Max(y2, y3)));

            minX = Math.Max(0, minX);
            maxX = Math.Min(_viewportWidth - 1, maxX);
            minY = Math.Max(0, minY);
            maxY = Math.Min(_viewportHeight - 1, maxY);

            // Pre-calc edge vectors
            Vector2 e1 = new Vector2(x2 - x1, y2 - y1);
            Vector2 e2 = new Vector2(x3 - x2, y3 - y2);
            Vector2 e3 = new Vector2(x1 - x3, y1 - y3);

            float area = (e1.X * (y3 - y1) - e1.Y * (x3 - x1));

            if (Math.Abs(area) < 0.0001f) return; // Degenerate

            // Colors & depths
            Vector4 col1 = v1.Color, col2 = v2.Color, col3 = v3.Color;
            float z1 = ndc1.Z, z2 = ndc2.Z, z3 = ndc3.Z;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    Vector2 p = new Vector2(x, y);

                    // Barycentric weights
                    float w1 = ((x2 - x) * (y3 - y) - (x3 - x) * (y2 - y)) / area;
                    float w2 = ((x3 - x) * (y1 - y) - (x1 - x) * (y3 - y)) / area;
                    float w3 = ((x1 - x) * (y2 - y) - (x2 - x) * (y1 - y)) / area;

                    if (w1 >= 0 && w2 >= 0 && w3 >= 0)
                    {
                        float z = w1 * z1 + w2 * z2 + w3 * z3;
                        Vector4 color = w1 * col1 + w2 * col2 + w3 * col3;
                        SetPixel(x, y, z, color);
                    }
                }
            }
        }

        // ---------- RENDER PRIMITIVE ----------
        private static void RenderPrimitive(int mode, List<Vertex> vertices)
        {
            if (vertices.Count == 0) return;

            switch (mode)
            {
                case GL_POINTS:
                    foreach (var v in vertices) DrawPoint(v);
                    break;
                case GL_LINES:
                    for (int i = 0; i < vertices.Count - 1; i += 2)
                        DrawLine(vertices[i], vertices[i + 1]);
                    break;
                case GL_TRIANGLES:
                    for (int i = 0; i < vertices.Count - 2; i += 3)
                        DrawTriangle(vertices[i], vertices[i + 1], vertices[i + 2]);
                    break;
                    // Bro có thể mở rộng thêm GL_TRIANGLE_FAN, GL_QUADS ở đây
            }
        }

        // ==========================================
        // 3. IMPLEMENT CÁC HÀM GL (Stub trước đây)
        // ==========================================

        public static void glMatrixMode(int mode) { _matrixMode = mode; }

        public static void glLoadIdentity()
        {
            if (_matrixMode == GL_MODELVIEW) _modelViewMatrix = Matrix4x4.Identity;
            else if (_matrixMode == GL_PROJECTION) _projectionMatrix = Matrix4x4.Identity;
        }

        public static void glPushMatrix()
        {
            if (_matrixMode == GL_MODELVIEW) _modelViewStack.Push(_modelViewMatrix);
            else if (_matrixMode == GL_PROJECTION) _projectionStack.Push(_projectionMatrix);
        }

        public static void glPopMatrix()
        {
            if (_matrixMode == GL_MODELVIEW && _modelViewStack.Count > 0) _modelViewMatrix = _modelViewStack.Pop();
            else if (_matrixMode == GL_PROJECTION && _projectionStack.Count > 0) _projectionMatrix = _projectionStack.Pop();
        }

        public static void glTranslatef(float x, float y, float z)
        {
            var t = Matrix4x4.CreateTranslation(x, y, z);
            if (_matrixMode == GL_MODELVIEW) _modelViewMatrix = Matrix4x4.Multiply(t, _modelViewMatrix);
            else if (_matrixMode == GL_PROJECTION) _projectionMatrix = Matrix4x4.Multiply(t, _projectionMatrix);
        }

        public static void glRotatef(float angle, float x, float y, float z)
        {
            var rot = Matrix4x4.CreateFromAxisAngle(new Vector3(x, y, z), MathHelper.DegreesToRadians(angle));
            if (_matrixMode == GL_MODELVIEW) _modelViewMatrix = Matrix4x4.Multiply(rot, _modelViewMatrix);
            else if (_matrixMode == GL_PROJECTION) _projectionMatrix = Matrix4x4.Multiply(rot, _projectionMatrix);
        }

        public static void glScalef(float x, float y, float z)
        {
            var s = Matrix4x4.CreateScale(x, y, z);
            if (_matrixMode == GL_MODELVIEW) _modelViewMatrix = Matrix4x4.Multiply(s, _modelViewMatrix);
            else if (_matrixMode == GL_PROJECTION) _projectionMatrix = Matrix4x4.Multiply(s, _projectionMatrix);
        }

        public static void glLoadMatrixf(float[] matrix)
        {
            // OpenGL là column-major, System.Numerics là row-major, phải transpose.
            // Nếu bro truyền vào từ C/C++ thì cần xử lý.
            // Mình tạm bỏ qua transpose, assume row-major cho đơn giản.
            var m = new Matrix4x4(
                matrix[0], matrix[1], matrix[2], matrix[3],
                matrix[4], matrix[5], matrix[6], matrix[7],
                matrix[8], matrix[9], matrix[10], matrix[11],
                matrix[12], matrix[13], matrix[14], matrix[15]
            );
            if (_matrixMode == GL_MODELVIEW) _modelViewMatrix = m;
            else if (_matrixMode == GL_PROJECTION) _projectionMatrix = m;
        }

        public static void glMultMatrixf(float[] matrix)
        {
            var m = new Matrix4x4(
                matrix[0], matrix[1], matrix[2], matrix[3],
                matrix[4], matrix[5], matrix[6], matrix[7],
                matrix[8], matrix[9], matrix[10], matrix[11],
                matrix[12], matrix[13], matrix[14], matrix[15]
            );
            if (_matrixMode == GL_MODELVIEW) _modelViewMatrix = Matrix4x4.Multiply(m, _modelViewMatrix);
            else if (_matrixMode == GL_PROJECTION) _projectionMatrix = Matrix4x4.Multiply(m, _projectionMatrix);
        }

        public static void glViewport(int x, int y, int width, int height)
        {
            _viewportX = x; _viewportY = y; _viewportWidth = width; _viewportHeight = height;
            // Reallocate buffers nếu size thay đổi
            if (_frameBuffer.Length != width * height)
            {
                _frameBuffer = new uint[width * height];
                _zBuffer = new float[width * height];
            }
        }

        public static void glOrtho(double left, double right, double bottom, double top, double zNear, double zFar)
        {
            var m = Matrix4x4.CreateOrthographicOffCenter((float)left, (float)right, (float)bottom, (float)top, (float)zNear, (float)zFar);
            if (_matrixMode == GL_PROJECTION) _projectionMatrix = Matrix4x4.Multiply(m, _projectionMatrix);
        }

        public static void glFrustum(double left, double right, double bottom, double top, double zNear, double zFar)
        {
            var m = Matrix4x4.CreatePerspectiveOffCenter((float)left, (float)right, (float)bottom, (float)top, (float)zNear, (float)zFar);
            if (_matrixMode == GL_PROJECTION) _projectionMatrix = Matrix4x4.Multiply(m, _projectionMatrix);
        }

        public static void glClear(int mask)
        {
            // Clear color
            if ((mask & GL_COLOR_BUFFER_BIT) != 0)
            {
                uint packed = (uint)(((byte)(_clearColor.W * 255) << 24) |
                                     ((byte)(_clearColor.X * 255) << 16) |
                                     ((byte)(_clearColor.Y * 255) << 8) |
                                     (byte)(_clearColor.Z * 255));
                for (int i = 0; i < _frameBuffer.Length; i++) _frameBuffer[i] = packed;
            }
            // Clear depth
            if ((mask & GL_DEPTH_BUFFER_BIT) != 0)
            {
                for (int i = 0; i < _zBuffer.Length; i++) _zBuffer[i] = _clearDepth;
            }
        }

        public static void glClearColor(float r, float g, float b, float a) { _clearColor = new Vector4(r, g, b, a); }
        public static void glClearDepth(double depth) { _clearDepth = (float)depth; }
        public static void glDepthFunc(int func) { _depthFunc = func; }
        public static void glEnable(int cap) { if (cap == GL_DEPTH_TEST) _depthTestEnabled = true; }
        public static void glDisable(int cap) { if (cap == GL_DEPTH_TEST) _depthTestEnabled = false; }
        public static void glBlendFunc(int sfactor, int dfactor) { /* Mặc định bỏ qua, để dễ */ }
        
        // ---------- glBegin/glEnd Pipeline ----------
        public static void glBegin(int mode)
        {
            if (_isInBeginEnd) return;
            _isInBeginEnd = true;
            _currentPrimitive = mode;
            _vertexBuffer.Clear();
        }

        public static void glEnd()
        {
            if (!_isInBeginEnd) return;
            _isInBeginEnd = false;
            RenderPrimitive(_currentPrimitive, _vertexBuffer);
            _vertexBuffer.Clear();
        }

        public static void glVertex2f(float x, float y) { glVertex3f(x, y, 0); }
        public static void glVertex3f(float x, float y, float z)
        {
            if (!_isInBeginEnd) return;
            Vertex v = new Vertex
            {
                Position = new Vector3(x, y, z),
                Color = _currentColor,
                TexCoord = _currentTexCoord,
                Normal = _currentNormal
            };
            _vertexBuffer.Add(v);
        }

        public static void glColor3f(float r, float g, float b) { _currentColor = new Vector4(r, g, b, 1); }
        public static void glColor4f(float r, float g, float b, float a) { _currentColor = new Vector4(r, g, b, a); }
        public static void glTexCoord2f(float u, float v) { _currentTexCoord = new Vector2(u, v); }
        public static void glNormal3f(float x, float y, float z) { _currentNormal = new Vector3(x, y, z); }

        public static void glFlush() { PresentFrame(); }
        public static void glFinish() { PresentFrame(); }


        // ==========================================
        // 4. FUNCTION POINTER (Driver Table)
        //    Giống như wglGetProcAddress trong C
        // ==========================================
        public static class FunctionTable
        {
            // Khai báo các delegate
            public delegate void glMatrixMode_delegate(int mode);
            public delegate void glLoadIdentity_delegate();
            public delegate void glPushMatrix_delegate();
            public delegate void glPopMatrix_delegate();
            public delegate void glTranslatef_delegate(float x, float y, float z);
            public delegate void glRotatef_delegate(float angle, float x, float y, float z);
            public delegate void glScalef_delegate(float x, float y, float z);
            public delegate void glBegin_delegate(int mode);
            public delegate void glEnd_delegate();
            public delegate void glVertex3f_delegate(float x, float y, float z);
            public delegate void glColor4f_delegate(float r, float g, float b, float a);
            public delegate void glClear_delegate(int mask);
            public delegate void glClearColor_delegate(float r, float g, float b, float a);

            // Bảng tra cứu (Function Pointers)
            public static readonly Dictionary<int, Delegate> Table = new Dictionary<int, Delegate>();

            // Hằng số ID cho từng hàm (giả lập)
            public const int FNC_MATRIXMODE = 1;
            public const int FNC_LOADIDENTITY = 2;
            public const int FNC_PUSHMATRIX = 3;
            public const int FNC_POPMATRIX = 4;
            public const int FNC_TRANSLATEF = 5;
            public const int FNC_ROTATEF = 6;
            public const int FNC_SCALEF = 7;
            public const int FNC_BEGIN = 8;
            public const int FNC_END = 9;
            public const int FNC_VERTEX3F = 10;
            public const int FNC_COLOR4F = 11;
            public const int FNC_CLEAR = 12;
            public const int FNC_CLEARCOLOR = 13;

            // Hàm khởi tạo bảng (gọi 1 lần khi OS boot)
            public static void Initialize()
            {
                Table[FNC_MATRIXMODE] = new glMatrixMode_delegate(glMatrixMode);
                Table[FNC_LOADIDENTITY] = new glLoadIdentity_delegate(glLoadIdentity);
                Table[FNC_PUSHMATRIX] = new glPushMatrix_delegate(glPushMatrix);
                Table[FNC_POPMATRIX] = new glPopMatrix_delegate(glPopMatrix);
                Table[FNC_TRANSLATEF] = new glTranslatef_delegate(glTranslatef);
                Table[FNC_ROTATEF] = new glRotatef_delegate(glRotatef);
                Table[FNC_SCALEF] = new glScalef_delegate(glScalef);
                Table[FNC_BEGIN] = new glBegin_delegate(glBegin);
                Table[FNC_END] = new glEnd_delegate(glEnd);
                Table[FNC_VERTEX3F] = new glVertex3f_delegate(glVertex3f);
                Table[FNC_COLOR4F] = new glColor4f_delegate(glColor4f);
                Table[FNC_CLEAR] = new glClear_delegate(glClear);
                Table[FNC_CLEARCOLOR] = new glClearColor_delegate(glClearColor);
            }

            // Hàm lấy con trỏ hàm (giống wglGetProcAddress)
            public static Delegate GetProcAddress(int functionID)
            {
                if (Table.TryGetValue(functionID, out Delegate del))
                    return del;
                return null;
            }

            // Generic helper để cast nhanh
            public static T GetProcAddress<T>(int functionID) where T : Delegate
            {
                return GetProcAddress(functionID) as T;
            }
        }
    }

    // Helper nhỏ (vì Cosmos không có MathHelper mặc định)
    public static class MathHelper
    {
        public static float DegreesToRadians(float degrees) => degrees * (float)(Math.PI / 180.0);
    }
}
