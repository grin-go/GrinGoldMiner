__kernel void myKernel(__global int* result) {
 int tid = get_local_id(0);
 int ref1 = myArray[tid] * 1;
 myArray[tid + 1] = 2;
 int ref2 = myArray[tid] * 1;
 result[tid] = ref1 * ref2;
} 