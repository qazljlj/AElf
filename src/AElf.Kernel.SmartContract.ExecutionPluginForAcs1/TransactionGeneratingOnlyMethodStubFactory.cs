using System;
using System.Threading.Tasks;
using AElf.CSharp.Core;
using AElf.Types;
using Google.Protobuf;
using System.Collections.Generic;

namespace AElf.Kernel.SmartContract.ExecutionPluginForAcs1
{
    public class TransactionGeneratingOnlyMethodStubFactory : IMethodStubFactory
    {
        public Address Sender { get; set; }
        public Address ContractAddress { get; set; }

        public IMethodStub<TInput, TOutput> Create<TInput, TOutput>(Method<TInput, TOutput> method)
            where TInput : IMessage<TInput>, new() where TOutput : IMessage<TOutput>, new()
        {
            async Task<IExecutionResult<TOutput>> SendAsync(TInput input)
            {
                return new ExecutionResult<TOutput>()
                {
                    Transaction = new Transaction()
                    {
                        From = Sender,
                        To = ContractAddress,
                        MethodName = method.Name,
                        Params = ByteString.CopyFrom(method.RequestMarshaller.Serializer(input))
                    }
                };
            }

            async Task<TOutput> CallAsync(TInput input)
            {
                throw new NotSupportedException();
            }

            return new MethodStub<TInput, TOutput>(method, SendAsync, CallAsync);
        }

        public IMethodStub<TInput, TOutput> Create<TInput, TOutput>(List<Method<TInput, TOutput>> methods)
            where TInput : IMessage<TInput>, new() where TOutput : IMessage<TOutput>, new()
        {
            async Task<IExecutionResult<TOutput>> SendAsync(TInput input)
            {
                throw new NotImplementedException();
            }

            async Task<List<TOutput>> CallAsync(TInput input)
            {
                throw new NotImplementedException();
            }
            throw new NotImplementedException();
        }
    }
}