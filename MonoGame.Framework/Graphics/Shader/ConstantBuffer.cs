using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#if MONOMAC
using MonoMac.OpenGL;
#elif WINDOWS || LINUX
using OpenTK.Graphics.OpenGL;
#elif GLES
using OpenTK.Graphics.ES20;
#elif PSS
using Sce.PlayStation.Core.Graphics;
#endif

namespace Microsoft.Xna.Framework.Graphics
{
    internal class ConstantBuffer : GraphicsResource
    {
        private readonly byte[] _buffer;

        private readonly int[] _parameters;

        private readonly int[] _offsets;

        private readonly string _name;

        private ulong _stateKey;

        private bool _dirty;

#if DIRECTX

        private SharpDX.Direct3D11.Buffer _cbuffer;

#elif OPENGL

        private int _program = -1;
        private int _location;

        /// <summary>
        /// A hash value which can be used to compare constant buffers.
        /// </summary>
        internal int HashKey { get; private set; }

#endif

        public ConstantBuffer(ConstantBuffer cloneSource)
        {
            GraphicsDevice = cloneSource.GraphicsDevice;

            // Share the immutable types.
            _name = cloneSource._name;
            _parameters = cloneSource._parameters;
            _offsets = cloneSource._offsets;

            // Clone the mutable types.
            _buffer = (byte[])cloneSource._buffer.Clone();
            Initialize();
        }

        public ConstantBuffer(GraphicsDevice device,
                              int sizeInBytes,
                              int[] parameterIndexes,
                              int[] parameterOffsets,
                              string name)
        {
            GraphicsDevice = device;

            _buffer = new byte[sizeInBytes];

            _parameters = parameterIndexes;
            _offsets = parameterOffsets;

            _name = name;

            Initialize();
        }

        private void Initialize()
        {
#if DIRECTX

            // Allocate the hardware constant buffer.
            var desc = new SharpDX.Direct3D11.BufferDescription();
            desc.SizeInBytes = _buffer.Length;
            desc.Usage = SharpDX.Direct3D11.ResourceUsage.Default;
            desc.BindFlags = SharpDX.Direct3D11.BindFlags.ConstantBuffer;
            desc.CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.None;
            _cbuffer = new SharpDX.Direct3D11.Buffer(GraphicsDevice._d3dDevice, desc);

#elif OPENGL 

            var data = new byte[_parameters.Length];
            for (var i = 0; i < _parameters.Length; i++)
            {
                data[i] = (byte)(_parameters[i] | _offsets[i]);
            }

            HashKey = MonoGame.Utilities.Hash.ComputeHash(data);

#endif
        }

        internal void Clear()
        {
#if OPENGL
            // Force the uniform location to be looked up again
            _program = -1;
#endif
        }

        private void SetData(int offset, int rows, int columns, int registers, object data)
        {
            // TODO: Should i pass the element size in?
            const int elementSize = 4;
            const int registerSize = elementSize * 4;

            // If the compiler cropped unused rows, clamp to register count
            // Note : array elements say they have "0 registers", that's wrong
            if (registers > 0)
                rows = Math.Min(registers, rows);

            // Take care of a single data type.
            // (only if it's not wrapped inside an array)
            if (rows == 1 && columns == 1)
            {
                // TODO: Consider storing all data in arrays to avoid
                // having to generate this temp array on every set.
                byte[] bytes;

                if (data is float)
                    bytes = BitConverter.GetBytes((float)data);
                else
                    bytes = BitConverter.GetBytes(((float[])data)[0]);

                Buffer.BlockCopy(bytes, 0, _buffer, offset, elementSize);
            }

            // Take care of the single copy case!
            else if (rows == 1 || columns == 4)
            {
                var source = data as Array;
                var actualRows = source.Length / columns;

                Buffer.BlockCopy(data as Array, 0, _buffer, offset, Math.Min(rows, actualRows) * columns * elementSize);
            }
            else
            {
                var source = data as Array;
                var actualRows = source.Length / columns;
                var sourceStride = columns * elementSize;

                rows = Math.Min(rows, actualRows);

                for (var y = 0; y < rows; y++)
                    Buffer.BlockCopy(source, sourceStride * y, _buffer, offset + registerSize * y, columns * elementSize);
            }
        }

        private void SetParameter(int offset, EffectParameter param)
        {
            if (param.ParameterType != EffectParameterType.Single)
                throw new NotImplementedException("Not supported!");

            if (param.Data == null) return;

            if (param.Elements.Count > 0)
                SetData(offset, param.RowCount * param.Elements.Count, param.ColumnCount, 0, param.Data);
            else
                SetData(offset, param.RowCount, param.ColumnCount, param.RegisterCount, param.Data);
        }

        public void Update(EffectParameterCollection parameters)
        {
            // TODO:  We should be doing some sort of dirty state 
            // testing here.
            //
            // It should let us skip all parameter updates if
            // nothing has changed.  It should not be per-parameter
            // as that is why you should use multiple constant
            // buffers.

            // If our state key becomes larger than the 
            // next state key then the keys have rolled 
            // over and we need to reset.
            if (_stateKey > EffectParameter.NextStateKey)
                _stateKey = 0;
            
            for (var p = 0; p < _parameters.Length; p++)
            {
                var index = _parameters[p];
                var param = parameters[index];

                if (param.StateKey < _stateKey)
                    continue;

                var offset = _offsets[p];
                _dirty = true;

                SetParameter(offset, param);
            }

            _stateKey = EffectParameter.NextStateKey;
        }

#if DIRECTX

        internal void Apply(GraphicsDevice device, ShaderStage stage, int slot)
        {
            // NOTE: We make the assumption here that the caller has
            // locked the d3dContext for us to use.
            var d3dContext = GraphicsDevice._d3dContext;

            // Update the hardware buffer.
            if (_dirty)
            {
                d3dContext.UpdateSubresource(_buffer, _cbuffer);
                _dirty = false;
            }
            
            // Set the buffer to the right stage.
            if (stage == ShaderStage.Vertex)
                d3dContext.VertexShader.SetConstantBuffer(slot, _cbuffer);
            else
                d3dContext.PixelShader.SetConstantBuffer(slot, _cbuffer);
        }

#elif OPENGL || PSS

        public unsafe void Apply(GraphicsDevice device, int program)
        {
#if OPENGL
            // NOTE: We assume here the program has 
            // already been set on the device.

            // If the program changed then lookup the
            // uniform again and apply the state.
            if (_program != program)
            {
                var location = GL.GetUniformLocation(program, _name);
                GraphicsExtensions.CheckGLError();
                if (location == -1)
                    return;

                _program = program;
                _location = location;
                _dirty = true;
            }

            // If the buffer content hasn't changed then we're
            // done... use the previously set uniform state.
            // NOTE: Commented out as workaround for caching mismatch between
            // OpenGL shader objects & constant buffers
            //if (!_dirty)
            //return;

            fixed (byte* bytePtr = _buffer)
            {
                // TODO: We need to know the type of buffer float/int/bool
                // and cast this correctly... else it doesn't work as i guess
                // GL is checking the type of the uniform.

                GL.Uniform4(_location, _buffer.Length / 16, (float*)bytePtr);
                GraphicsExtensions.CheckGLError();
            }

            // Clear the dirty flag.
            _dirty = false;
#endif
            
#if PSS
#warning Unimplemented
#endif
        }

#endif

    }
}
