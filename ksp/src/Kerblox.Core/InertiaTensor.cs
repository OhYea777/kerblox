using System;
using System.Collections.Generic;

namespace Kerblox.Core
{
    /// <summary>Unit quaternion, so Core can hand Unity a rotation without referencing it.</summary>
    public readonly struct Quat
    {
        public readonly float X, Y, Z, W;

        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        public static readonly Quat Identity = new Quat(0, 0, 0, 1);

        /// <summary>Rotates <paramref name="v"/> by this quaternion (same convention as Unity's <c>q * v</c>).</summary>
        public Float3 Rotate(Float3 v)
        {
            var u = new Float3(X, Y, Z);
            Float3 t = Float3.Cross(u, v) * 2f;
            return v + t * W + Float3.Cross(u, t);
        }

        public override string ToString() => $"({X:0.####}, {Y:0.####}, {Z:0.####}, {W:0.####})";
    }

    /// <summary>
    /// Inertia tensor of a grid about its centre of mass, in part-local axes, plus its
    /// principal decomposition in the form Unity's Rigidbody takes it:
    /// <c>inertiaTensor = PrincipalMoments</c>, <c>inertiaTensorRotation = PrincipalRotation</c>,
    /// i.e. I = R · diag(PrincipalMoments) · Rᵀ.
    ///
    /// Why: Unity derives the tensor from colliders assuming uniform density, so an
    /// iron-bottomed grid gets a tensor about the wrong point with the wrong mass
    /// distribution. Units are tonnes·m² to match KSP's rigidbody masses.
    /// </summary>
    public readonly struct InertiaTensor
    {
        /// <summary>Tonnes.</summary>
        public readonly double Mass;

        /// <summary>Part-local metres, relative to the grid's bounding-box centre.</summary>
        public readonly Float3 CenterOfMass;

        /// <summary>Tensor components about <see cref="CenterOfMass"/>; Ixy etc. carry the sign of the tensor (−Σ m·x·y).</summary>
        public readonly double Ixx, Iyy, Izz, Ixy, Ixz, Iyz;

        /// <summary>Eigenvalues of the tensor, ordered to match the columns of <see cref="PrincipalRotation"/>.</summary>
        public readonly Float3 PrincipalMoments;

        /// <summary>Rotation from principal axes to part-local axes.</summary>
        public readonly Quat PrincipalRotation;

        private InertiaTensor(double mass, Float3 com, double ixx, double iyy, double izz,
            double ixy, double ixz, double iyz, Float3 moments, Quat rotation)
        {
            Mass = mass; CenterOfMass = com;
            Ixx = ixx; Iyy = iyy; Izz = izz; Ixy = ixy; Ixz = ixz; Iyz = iyz;
            PrincipalMoments = moments; PrincipalRotation = rotation;
        }

        public static readonly InertiaTensor Zero =
            new InertiaTensor(0, Float3.Zero, 0, 0, 0, 0, 0, 0, Float3.Zero, Quat.Identity);

        /// <summary>
        /// Per-cell mass in tonnes from the registry's block densities, resolved once per
        /// palette entry. Unknown and non-solid states weigh nothing, matching
        /// <see cref="MassProperties"/>. This is the only place inertia depends on how
        /// cells map to block types.
        /// </summary>
        public static Func<int, int, int, double> RegistryCellMass(VoxelGrid grid, BlockRegistry registry, BlockLayout layout)
        {
            double volume = (double)layout.BlockSize * layout.BlockSize * layout.BlockSize;
            var massByState = new Dictionary<BlockState, double>();
            foreach (BlockState state in grid.Palette)
            {
                BlockType t = registry.Get(state);
                massByState[state] = t != null && t.Solid ? t.Density * volume : 0;
            }
            return (x, y, z) => massByState.TryGetValue(grid.Get(x, y, z), out double m) ? m : 0;
        }

        public static InertiaTensor Compute(VoxelGrid grid, BlockRegistry registry, BlockLayout layout) =>
            Compute(grid.SizeX, grid.SizeY, grid.SizeZ, layout, RegistryCellMass(grid, registry, layout));

        /// <summary>
        /// Treats every cell with positive mass as a uniform solid cube of edge
        /// <see cref="BlockLayout.BlockSize"/>: its own m·s²/6 about each axis, plus the
        /// parallel-axis term for its offset from the grid's CoM.
        /// </summary>
        public static InertiaTensor Compute(int sizeX, int sizeY, int sizeZ, BlockLayout layout, Func<int, int, int, double> cellMass)
        {
            if (cellMass == null) throw new ArgumentNullException(nameof(cellMass));

            // Pass 1: mass and CoM. Pass 2 is about the CoM rather than using
            // I_com = I_origin − M·d², which loses precision for large offset grids.
            double mass = 0, mx = 0, my = 0, mz = 0;
            for (int y = 0; y < sizeY; y++)
                for (int z = 0; z < sizeZ; z++)
                    for (int x = 0; x < sizeX; x++)
                    {
                        double m = cellMass(x, y, z);
                        if (!(m > 0)) continue;
                        Float3 c = layout.CellCenter(x, y, z);
                        mass += m; mx += m * c.X; my += m * c.Y; mz += m * c.Z;
                    }
            if (!(mass > 0)) return Zero;

            double cx = mx / mass, cy = my / mass, cz = mz / mass;
            double s = layout.BlockSize;
            double self = s * s / 6.0;
            double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;   // Σm·x², Σm·x·y, ...
            double selfSum = 0;
            for (int y = 0; y < sizeY; y++)
                for (int z = 0; z < sizeZ; z++)
                    for (int x = 0; x < sizeX; x++)
                    {
                        double m = cellMass(x, y, z);
                        if (!(m > 0)) continue;
                        Float3 c = layout.CellCenter(x, y, z);
                        double dx = c.X - cx, dy = c.Y - cy, dz = c.Z - cz;
                        xx += m * dx * dx; yy += m * dy * dy; zz += m * dz * dz;
                        xy += m * dx * dy; xz += m * dx * dz; yz += m * dy * dz;
                        selfSum += m * self;
                    }

            double ixx = yy + zz + selfSum, iyy = xx + zz + selfSum, izz = xx + yy + selfSum;
            double ixy = -xy, ixz = -xz, iyz = -yz;

            var a = new[,] { { ixx, ixy, ixz }, { ixy, iyy, iyz }, { ixz, iyz, izz } };
            Diagonalize(a, out double[] eig, out double[,] v);

            return new InertiaTensor(mass, new Float3((float)cx, (float)cy, (float)cz),
                ixx, iyy, izz, ixy, ixz, iyz,
                new Float3((float)eig[0], (float)eig[1], (float)eig[2]),
                ToQuat(v));
        }

        /// <summary>
        /// Cyclic Jacobi eigen-decomposition of a symmetric 3×3 matrix. Columns of
        /// <paramref name="vectors"/> are the eigenvectors, forming a proper rotation.
        /// </summary>
        private static void Diagonalize(double[,] a, out double[] values, out double[,] vectors)
        {
            var v = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
                double diag = a[0, 0] * a[0, 0] + a[1, 1] * a[1, 1] + a[2, 2] * a[2, 2];
                if (off <= 1e-30 * diag || off == 0) break;

                for (int p = 0; p < 2; p++)
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (a[p, q] == 0) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        double c = 1 / Math.Sqrt(t * t + 1), sn = t * c;

                        // a = Jᵀ a J, v = v J, with J the Givens rotation in the (p, q) plane.
                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - sn * akq;
                            a[k, q] = sn * akp + c * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - sn * aqk;
                            a[q, k] = sn * apk + c * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - sn * vkq;
                            v[k, q] = sn * vkp + c * vkq;
                        }
                    }
            }

            values = new[] { a[0, 0], a[1, 1], a[2, 2] };
            vectors = v;
            // Jacobi rotations keep det(v) = +1, so v is already a proper rotation.
        }

        /// <summary>Rotation matrix (columns = rotated basis vectors) to quaternion.</summary>
        private static Quat ToQuat(double[,] m)
        {
            double trace = m[0, 0] + m[1, 1] + m[2, 2];
            double x, y, z, w;
            if (trace > 0)
            {
                double s = Math.Sqrt(trace + 1) * 2;
                w = s / 4; x = (m[2, 1] - m[1, 2]) / s; y = (m[0, 2] - m[2, 0]) / s; z = (m[1, 0] - m[0, 1]) / s;
            }
            else if (m[0, 0] > m[1, 1] && m[0, 0] > m[2, 2])
            {
                double s = Math.Sqrt(1 + m[0, 0] - m[1, 1] - m[2, 2]) * 2;
                w = (m[2, 1] - m[1, 2]) / s; x = s / 4; y = (m[0, 1] + m[1, 0]) / s; z = (m[0, 2] + m[2, 0]) / s;
            }
            else if (m[1, 1] > m[2, 2])
            {
                double s = Math.Sqrt(1 + m[1, 1] - m[0, 0] - m[2, 2]) * 2;
                w = (m[0, 2] - m[2, 0]) / s; x = (m[0, 1] + m[1, 0]) / s; y = s / 4; z = (m[1, 2] + m[2, 1]) / s;
            }
            else
            {
                double s = Math.Sqrt(1 + m[2, 2] - m[0, 0] - m[1, 1]) * 2;
                w = (m[1, 0] - m[0, 1]) / s; x = (m[0, 2] + m[2, 0]) / s; y = (m[1, 2] + m[2, 1]) / s; z = s / 4;
            }
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            return new Quat((float)(x / n), (float)(y / n), (float)(z / n), (float)(w / n));
        }

        public override string ToString() =>
            $"principal {PrincipalMoments} t·m², rotation {PrincipalRotation}";
    }
}
